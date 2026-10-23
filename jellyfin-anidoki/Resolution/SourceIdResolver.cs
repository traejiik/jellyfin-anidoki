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
public sealed class SourceIdResolver(IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths,
    ILoggerFactory loggerFactory, ILogger logger) {
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
            logger.LogDebug("[anidoki-diag] Series '{SeriesName}' providers: {SeriesProviderIds}", episode.Series?.Name, Dump(episode.Series?.ProviderIds));
            logger.LogDebug("[anidoki-diag] Season {SeasonIndexNumber} providers: {SeasonProviderIds}", episode.Season?.IndexNumber, Dump(episode.Season?.ProviderIds));
            logger.LogDebug("[anidoki-diag] Episode {EpisodeIndexNumber} '{EpisodeName}' providers: {EpisodeProviderIds}", episode.IndexNumber, episode.Name, Dump(episode.ProviderIds));
            logger.LogDebug("[anidoki-diag] Series contains {SeasonCount} season(s)", episode.Series?.Children.OfType<Season>().Count());
            logger.LogDebug("[anidoki-diag] Titles -> name: '{EpisodeSeriesName}', originalTitle: '{SeriesOriginalTitle}'", episode.SeriesName, episode.Series?.OriginalTitle ?? "<none>");
        }

        // An episode whose series cannot be found has no IDs to look up; title search still runs.
        Series? series = episode?.Series;
        if (episode != null && series == null)
            return new SourceIds(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), null);

        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null;
        (int? aniDbId, int? episodeOffset) aniDbId = (null, null);
        if (source.Item.ProviderIds != null && source.FallbackAniListId is int retrievedAniListId) {
            logger.LogDebug("AniList ID found. Retrieving provider IDs from offline database...");
            ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(httpClientFactory.CreateClient(NamedClient.Default), logger, retrievedAniListId, AnimeOfflineDatabaseHelpers.Source.Anilist);
            if (ids is null) {
                ids = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                    Anilist = retrievedAniListId
                };
                logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniList ID");
            } else {
                logger.LogDebug("Retrieved provider IDs");
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
                logger.LogDebug("Retrieving provider IDs from offline database for AniDb ID {AniDbId}...", aniDbId.aniDbId.Value);
                ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(httpClientFactory.CreateClient(NamedClient.Default), logger, aniDbId.aniDbId.Value, AnimeOfflineDatabaseHelpers.Source.Anidb);
                if (ids is null) {
                    ids = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                        AniDb = aniDbId.aniDbId
                    };
                    logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniDb ID");
                } else {
                    // Distinguish "ARM gave us a usable ID" from "ARM gave us a row
                    // that carries none". Only the former avoids the title-search
                    // fallback; reporting both as success would make the fallback look
                    // like a bug in ID resolution rather than a gap in the database.
                    if (ids.Anilist is null or 0 && ids.MyAnimeList is null or 0 && ids.Kitsu is null or 0) {
                        logger.LogWarning("ARM server has a record for AniDb ID {AniDbId} but no tracker IDs; falling back to title search", aniDbId.aniDbId.Value);
                    } else {
                        logger.LogDebug("Retrieved provider IDs");
                    }
                }
            }
        }

        return new SourceIds(ids ?? new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), aniDbId.episodeOffset);
    }
}
