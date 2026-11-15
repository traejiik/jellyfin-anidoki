using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Resolution;

/// <summary>Tracker IDs the ID databases give for a source, and the episode offset the anime list applies.</summary>
/// <param name="AniDbOffset">The anime list's episode offset for this season, or null when it gave none.</param>
public sealed record SourceIds(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse Ids, int? AniDbOffset) {
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
}

/// <summary>Looks up a source's tracker IDs through the ARM server and the anime list. Logs as its caller.</summary>
/// <param name="animeList">The anime list's TMDB movie IDs; the shared index unless a test gives its own.</param>
public sealed class SourceIdResolver(IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths,
    ILoggerFactory loggerFactory, ILogger logger, AnimeListIndex? animeList = null) {
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
            return new SourceIds(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), null);

        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null;
        (int? aniDbId, int? episodeOffset) aniDbId = (null, null);
        if (source.Item.ProviderIds != null && source.FallbackAniListId is int retrievedAniListId) {
            logger.LogDebug("Looking up tracker IDs for AniList ID {AniListId}", retrievedAniListId);
            ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(httpClientFactory.CreateClient(NamedClient.Default), logger, retrievedAniListId, AnimeOfflineDatabaseHelpers.Source.Anilist);
            if (ids is null) {
                ids = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                    Anilist = retrievedAniListId
                };
                logger.LogDebug("ARM has no IDs for AniList ID {AniListId}; using that ID alone", retrievedAniListId);
            } else {
                logger.LogDebug("Found tracker IDs for AniList ID {AniListId}", retrievedAniListId);
            }
        } else if (episode != null
                       ? (AnimeListHelpers.HasProviderId(series!.ProviderIds, "Tvdb") ||
                          AnimeListHelpers.HasProviderId(episode.Season?.ProviderIds, "Anidb") ||
                          AnimeListHelpers.HasProviderId(series.ProviderIds, "Anidb"))
                       : source.Item.ProviderIds != null &&
                         AnimeListHelpers.HasProviderId(source.Item.ProviderIds, "Anidb")) {
            AnimeListHelpers.AnimeListXml animeListXml = await AnimeListHelpers.GetAnimeListFileContents(logger, loggerFactory, httpClientFactory, applicationPaths);
            aniDbId = await AnimeListHelpers.GetAniDbId(logger, source.Item, source.EpisodeNumber, source.SeasonNumber, animeListXml);
            if (aniDbId.aniDbId != null) {
                logger.LogDebug("Looking up tracker IDs for AniDB ID {AniDbId}", aniDbId.aniDbId.Value);
                ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(httpClientFactory.CreateClient(NamedClient.Default), logger, aniDbId.aniDbId.Value, AnimeOfflineDatabaseHelpers.Source.Anidb);
                if (ids is null) {
                    ids = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
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
                   && AnimeListHelpers.TryGetProviderId(source.Item.ProviderIds, "Tmdb", out string tmdbId)
                   && (animeList ?? AnimeListIndex.Shared).AniDbIdForMovie(tmdbId) is int movieAniDbId) {
            // A Mixed library's movie known only by TMDB: the anime list, which the library filter also checked,
            // maps it to AniDB. Other libraries keep resolving such movies by title, as before.
            logger.LogDebug("TMDB ID {TmdbId} maps to AniDB ID {AniDbId} in the anime list; looking up tracker IDs", tmdbId, movieAniDbId);
            ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(httpClientFactory.CreateClient(NamedClient.Default), logger, movieAniDbId, AnimeOfflineDatabaseHelpers.Source.Anidb)
                  ?? new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse { AniDb = movieAniDbId };
        }

        return new SourceIds(ids ?? new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), aniDbId.episodeOffset);
    }
}
