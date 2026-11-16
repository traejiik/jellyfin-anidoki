using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Resolution;

/// <summary>Tracker IDs the ID databases give for a source, and the episode offset the anime list applies.</summary>
/// <param name="AniDbOffset">The anime list's episode offset for this season, or null when it gave none.</param>
public sealed record SourceIds(ArmClient.OfflineDatabaseResponse Ids, int? AniDbOffset) {
    /// <summary>
    /// Movies have no season, so resolved IDs apply to them directly. Specials (season 0) keep using title search,
    /// which finds the OVA by name.
    /// </summary>
    public bool UseForSource(SyncSource source) => source.Kind == MediaKind.Movie || source.SeasonNumber != 0;

    // The ID path and the title path apply the offset differently, as they always have: the ID path subtracts it
    // unconditionally, the title path only when it is smaller than the episode. PR 13's planner replaces both with
    // mapping-driven numbers.

    /// <summary>The ID path's episode: the offset applies unconditionally.</summary>
    public int IdProgress(SyncSource source) =>
        source.Kind == MediaKind.Movie ? SyncSource.MovieProgress : source.EpisodeNumber - (AniDbOffset ?? 0);

    /// <summary>The title path's episode: the offset applies only when it is smaller than the episode.</summary>
    public int TitleProgress(SyncSource source) {
        if (source.Kind == MediaKind.Movie)
            return SyncSource.MovieProgress;
        int offset = AniDbOffset ?? 0;
        return offset < source.EpisodeNumber ? source.EpisodeNumber - offset : source.EpisodeNumber;
    }

    public ResolvedIds ToResolvedIds() => new(Ids.Anilist, Ids.MyAnimeList, Ids.Kitsu, Ids.AniDb);

    /// <summary>Why the source has no IDs when that is not just a gap in the ID databases, as an unresolved reason.</summary>
    public string? Problem { get; init; }

    /// <summary>The reason given when a movie's AniDB ID belongs to another title in the anime list.</summary>
    public const string AniDbConflict = "anidb-conflict";
}

/// <summary>Looks up a source's tracker IDs through the ARM server and the anime list. Logs as its caller.</summary>
public sealed class SourceIdResolver(ArmClient arm, AnimeListSource animeList, ILogger logger) {
    public async Task<SourceIds> Resolve(SyncSource source) {
        var episode = source.Item as Episode;
        // Diagnostics: prints exactly which provider IDs Jellyfin holds at
        // each level, plus the season count that drives the absolute-episode path.
        // Logged at Debug: this runs on every played episode, and the information
        // is only useful when investigating a mis-resolution. The IsEnabled guard
        // keeps the provider-ID dumps from being built when Debug is off.
        if (episode != null && logger.IsEnabled(LogLevel.Debug)) {
            string Dump(Dictionary<string, string>? ids) => ids == null || ids.Count == 0
                ? "<none>"
                : string.Join(", ", ids.Select(kv => $"{kv.Key}={kv.Value}"));
            logger.LogDebug("Series {SeriesName} has provider IDs {ProviderIds}", episode.Series?.Name, Dump(episode.Series?.ProviderIds));
            logger.LogDebug("Season {SeasonNumber} has provider IDs {ProviderIds}", episode.Season?.IndexNumber, Dump(episode.Season?.ProviderIds));
            logger.LogDebug("Episode {EpisodeNumber} ({ItemName}) has provider IDs {ProviderIds}", episode.IndexNumber, episode.Name, Dump(episode.ProviderIds));
            logger.LogDebug("Series {SeriesName} has {Count} seasons", episode.Series?.Name, episode.Series?.Children.OfType<Season>().Count());
            logger.LogDebug("The series is named {SeriesName}, originally {OriginalTitle}", episode.SeriesName, episode.Series?.OriginalTitle ?? "<none>");
        }

        // An episode whose series cannot be found has no IDs to look up; title search still runs.
        Series? series = episode?.Series;
        if (episode != null && series == null)
            return new SourceIds(new ArmClient.OfflineDatabaseResponse(), null);

        AnimeListIndex index = animeList.Index;
        MovieAniDbCheck? movieCheck = source.Item is Movie movie ? MovieAniDbCheck.For(movie, index) : null;
        ArmClient.OfflineDatabaseResponse? ids = null;
        (int? aniDbId, int? episodeOffset) aniDbId = (null, null);
        if (source.Item.ProviderIds != null && source.FallbackAniListId is int retrievedAniListId) {
            logger.LogDebug("Looking up tracker IDs for AniList ID {AniListId}", retrievedAniListId);
            ids = await arm.GetIds(retrievedAniListId, ArmClient.Source.Anilist);
            if (ids is null) {
                ids = new ArmClient.OfflineDatabaseResponse {
                    Anilist = retrievedAniListId
                };
                logger.LogDebug("ARM has no IDs for AniList ID {AniListId}; using that ID alone", retrievedAniListId);
            } else {
                logger.LogDebug("Found tracker IDs for AniList ID {AniListId}", retrievedAniListId);
            }
        } else if (movieCheck is { Verdict: MovieAniDbVerdict.Agrees or MovieAniDbVerdict.Replaced, AniDbId: int checkedAniDbId }) {
            if (movieCheck.Verdict == MovieAniDbVerdict.Replaced) {
                AnimeListHelpers.TryGetProviderId(source.Item.ProviderIds, "AniDB", out string? heldAniDbId);
                logger.LogInformation("The anime list gives {ItemName}'s TMDB ID AniDB ID {ListAniDbId}, not the {AniDbId} Jellyfin holds; using the anime list's",
                    source.Item.Name, checkedAniDbId, heldAniDbId);
            }
            ids = await LookUpAniDb(checkedAniDbId);
        } else if (movieCheck is { Verdict: MovieAniDbVerdict.Conflicts }) {
            AnimeListHelpers.TryGetProviderId(source.Item.ProviderIds, "AniDB", out string? heldAniDbId);
            logger.LogWarning("AniDB ID {AniDbId} on {ItemName} belongs to another title in the anime list; not syncing by it until the movie's metadata is fixed",
                heldAniDbId, source.Item.Name);
            return new SourceIds(new ArmClient.OfflineDatabaseResponse(), null) { Problem = SourceIds.AniDbConflict };
        } else if (movieCheck is { Verdict: MovieAniDbVerdict.Unconfirmed or MovieAniDbVerdict.NotListed } && source.IdsOnly) {
            logger.LogDebug("The anime list does not tie {ItemName}'s AniDB ID to its TMDB or IMDb ID; not using it in a Mixed library", source.Item.Name);
        } else if (episode != null
                       ? (AnimeListHelpers.HasProviderId(series!.ProviderIds, "Tvdb") ||
                          AnimeListHelpers.HasProviderId(episode.Season?.ProviderIds, "Anidb") ||
                          AnimeListHelpers.HasProviderId(series.ProviderIds, "Anidb"))
                       : source.Item.ProviderIds != null &&
                         AnimeListHelpers.HasProviderId(source.Item.ProviderIds, "Anidb")) {
            AnimeListHelpers.AnimeListXml? animeListXml = await animeList.Read();
            aniDbId = await AnimeListHelpers.GetAniDbId(logger, source.Item, source.EpisodeNumber, source.SeasonNumber, animeListXml);
            if (aniDbId.aniDbId != null) {
                logger.LogDebug("Looking up tracker IDs for AniDB ID {AniDbId}", aniDbId.aniDbId.Value);
                ids = await arm.GetIds(aniDbId.aniDbId.Value, ArmClient.Source.Anidb);
                if (ids is null) {
                    ids = new ArmClient.OfflineDatabaseResponse {
                        AniDb = aniDbId.aniDbId
                    };
                    logger.LogDebug("ARM has no IDs for AniDB ID {AniDbId}; using that ID alone", aniDbId.aniDbId.Value);
                } else {
                    // Distinguish "ARM gave us a usable ID" from "ARM gave us a row
                    // that carries none". Only the former avoids the title-search
                    // fallback; reporting both as success would make the fallback look
                    // like a bug in ID resolution rather than a gap in the database.
                    if (ids.Anilist is null or 0 && ids.MyAnimeList is null or 0 && ids.Kitsu is null or 0) {
                        logger.LogDebug("ARM knows AniDB ID {AniDbId} but lists no tracker IDs; falling back to title search", aniDbId.aniDbId.Value);
                    } else {
                        logger.LogDebug("Found tracker IDs for AniDB ID {AniDbId}", aniDbId.aniDbId.Value);
                    }
                }
            }
        } else if (source.IdsOnly && source.Kind == MediaKind.Movie
                   && AnimeListHelpers.TryGetProviderId(source.Item.ProviderIds, "Tmdb", out string? tmdbId)
                   && index.AniDbIdForMovie(tmdbId) is int movieAniDbId) {
            // A Mixed library's movie known only by TMDB: the anime list, which the library filter also checked,
            // maps it to AniDB. Other libraries keep resolving such movies by title, as before.
            logger.LogDebug("TMDB ID {TmdbId} maps to AniDB ID {AniDbId} in the anime list; looking up tracker IDs", tmdbId, movieAniDbId);
            ids = await LookUpAniDb(movieAniDbId);
        }

        return new SourceIds(ids ?? new ArmClient.OfflineDatabaseResponse(), aniDbId.episodeOffset);
    }

    /// <summary>A movie's tracker IDs by its AniDB ID; the AniDB ID alone when ARM has none.</summary>
    private async Task<ArmClient.OfflineDatabaseResponse> LookUpAniDb(int aniDbId) =>
        await arm.GetIds(aniDbId, ArmClient.Source.Anidb)
        ?? new ArmClient.OfflineDatabaseResponse { AniDb = aniDbId };
}
