using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Api.Anilist;
using jellyfin_anidoki.Api.Kitsu;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

public class Sync {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Sync> _logger;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ArmClient _arm;
    private readonly AnimeListSource _animeList;
    private readonly IUserDataManager _userDataManager;
    private readonly IMemoryCache _memoryCache;
    private readonly IAsyncDelayer _delayer;
    private readonly ApiName _apiName;
    private readonly SyncHelper.StatusFilter _status;
    private int _apiTimeOutLength = 2000;

    /// <summary>Tests set this to substitute an in-memory tracker list; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig?, ITrackerListSource>? ListSourceOverride { get; set; }

    public Sync(IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServerApplicationHost serverApplicationHost,
        IHttpContextAccessor httpContextAccessor,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ArmClient arm,
        AnimeListSource animeList,
        IUserDataManager userDataManager,
        IMemoryCache memoryCache,
        IAsyncDelayer delayer,
        ApiName apiName,
        SyncHelper.StatusFilter status) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _arm = arm;
        _animeList = animeList;
        _userDataManager = userDataManager;
        _delayer = delayer;
        _memoryCache = memoryCache;
        _apiName = apiName;
        _status = status;
        _logger = loggerFactory.CreateLogger<Sync>();
    }

    /// <summary>
    /// Sync Jellyfin with the selected provider's watch list. Runs inside an operation.
    /// </summary>
    /// <param name="userId">ID of the user that you want to update the library of.</param>
    public async Task SyncFromProvider(string userId, OperationContext operation) {
        if (Plugin.Instance?.StorageError is { } storageError) {
            _logger.LogWarning("({Provider}) The sync from the tracker was skipped because AniDoki storage is unavailable: {StorageError}", _apiName, storageError);
            operation.Result = OperationState.Failed;
            operation.Message = "AniDoki storage is unavailable, so nothing was imported.";
            return;
        }

        InboundPlanResult? plan = await Plan(Guid.Parse(userId), operation);
        if (plan == null) {
            _logger.LogWarning("({Provider}) The tracker returned no anime for user {UserId}; check that the account is linked and its list is not empty", _apiName, userId);
            operation.Result = OperationState.NoChange;
            operation.Message = "The tracker returned no anime for the selected statuses. Check that the account is linked.";
            return;
        }

        // PlanLibrary plans no season for a user Jellyfin does not know, so a missing user marks nothing.
        User? user = _userManager.GetUserById(Guid.Parse(userId));
        if (user != null) {
            long started = Stopwatch.GetTimestamp();
            foreach (InboundSeasonPlan season in plan.Seasons)
                Apply(user, season);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("({Provider}) Applied {Count} season plans in {ElapsedMs} ms", _apiName, plan.Seasons.Count, LogText.ElapsedMs(started));
        }
        operation.Result = OperationState.Succeeded;
        operation.Message = plan.EntryCount == 1 ? "Checked 1 tracker entry." : $"Checked {plan.EntryCount} tracker entries.";
    }

    /// <summary>
    /// Get the providers anime list.
    /// </summary>
    /// <param name="userId">ID of the user to get the anime list of.</param>
    /// <returns>Users' provider anime list.</returns>
    private async Task<List<Anime>?> GetAnimeList(string userId) {
        UserConfig? config = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(item => item.UserId == Guid.Parse(userId));
        // A user with no saved settings has no linked tracker, so there is nothing to read.
        ITrackerListSource? source = ListSourceOverride?.Invoke(_apiName, config) ?? (config == null ? null : _apiName switch {
            ApiName.Mal => new MalTrackerClient(new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            ApiName.AniList => new AniListTrackerClient(new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            ApiName.Kitsu => new KitsuTrackerClient(new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            _ => null
        });
        if (source == null)
            return null;

        MalApiCalls.User? user = new MalApiCalls.User();
        // MAL's list needs no user ID; AniList and Kitsu do.
        if (_apiName is ApiName.AniList or ApiName.Kitsu) {
            user = await source.GetUser();
            if (user == null || user.Id == 0) {
                _logger.LogWarning("({Provider}) Could not read the linked user of user {UserId}; nothing was read from the tracker", _apiName, userId);
                return null;
            }
        }

        switch (_status) {
            case SyncHelper.StatusFilter.Completed:
                return await source.GetAnimeList(Status.Completed, user?.Id);
            case SyncHelper.StatusFilter.Watching:
                return await source.GetAnimeList(Status.Watching, user?.Id);
            case SyncHelper.StatusFilter.Both:
                List<Anime>? completed = await source.GetAnimeList(Status.Completed, user?.Id);
                List<Anime>? watching = await source.GetAnimeList(Status.Watching, user?.Id);
                if (completed != null && watching != null) {
                    return completed.Concat(watching).ToList();
                }

                return completed ?? watching;
        }

        return null;
    }

    /// <summary>
    /// Reads the tracker list and the user's library and decides what to mark, without marking anything. Null when the
    /// tracker gave no list. The preview runs this alone; <see cref="SyncFromProvider"/> applies what it returns.
    /// </summary>
    internal async Task<InboundPlanResult?> Plan(Guid userId, OperationContext operation) {
        long started = Stopwatch.GetTimestamp();
        var completedList = await GetAnimeList(userId.ToString());
        if (completedList == null)
            return null;
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("({Provider}) Read {Count} tracker entries in {ElapsedMs} ms", _apiName, completedList.Count, LogText.ElapsedMs(started));

        started = Stopwatch.GetTimestamp();
        var (found, unresolved) = await GetMetadataIdsFromAnime(completedList, operation);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("({Provider}) Looked up IDs for {Count} tracker entries in {ElapsedMs} ms", _apiName, completedList.Count, LogText.ElapsedMs(started));

        started = Stopwatch.GetTimestamp();
        List<InboundSeasonPlan> seasons = await PlanLibrary(userId, found);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("({Provider}) Planned {Count} seasons from the library in {ElapsedMs} ms", _apiName, seasons.Count, LogText.ElapsedMs(started));
        return new InboundPlanResult(completedList.Count, seasons, unresolved);
    }

    private async Task<List<InboundSeasonPlan>> PlanLibrary(Guid userId, List<SyncAnimeMetadata> convertedWatchList) {
        var plans = new List<InboundSeasonPlan>();
        var userLibrary = SyncHelper.GetUsersJellyfinLibrary(userId, _userManager, _libraryManager, _logger);
        var user = _userManager.GetUserById(userId);
        if (user == null) {
            _logger.LogWarning("User {UserId} was not found in Jellyfin; nothing was planned", userId);
            return plans;
        }
        foreach (BaseItem baseItem in userLibrary) {
            if (SyncHelper.MediaShouldBeIgnored(baseItem)) {
                _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", baseItem.Name);
            } else if (baseItem is Series series) {
                (ArmClient.Source? source, int? providerId) = SyncHelper.GetSeriesProviderId(series);

                if (providerId != null && providerId != 0 && source != null) {
                    var seasons = await SyncHelper.GetJellyfinSeasons(convertedWatchList, providerId.Value, series, source.Value,
                        _logger, _arm, _animeList);
                    foreach ((Season season, SyncAnimeMetadata found) in seasons) {
                        if (season != null)
                            plans.Add(InboundPlanner.ForSeason(season, found, DateTime.UtcNow));
                    }
                } else {
                    _logger.LogDebug("Skipping {SeriesName}; it has no AniList or AniDB ID", series.Name);
                }
            }
        }
        return plans;
    }

    /// <summary>Reads the tracker list for the statuses this sync was built with. Null when the tracker gave none.</summary>
    internal Task<List<Anime>?> ReadList(Guid userId) => GetAnimeList(userId.ToString());

    private void Apply(User user, InboundSeasonPlan plan) {
        if (plan.SeasonIgnored) {
            _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", plan.Season.Name);
            return;
        }

        foreach (Episode ignored in plan.IgnoredEpisodes)
            _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", ignored.Name);
        Mark(user, plan.Season, plan.Episodes, plan.PlayedDate, plan.MarksSeason);
        _logger.LogInformation("({Provider}) Marked {Count} episodes of {SeriesName} season {SeasonNumber} as played for user {UserId}", _apiName, plan.Episodes.Count, plan.Season.SeriesName, plan.Season.IndexNumber, user.Id);
    }

    /// <summary>
    /// Marks the episodes played on <paramref name="playedDate"/>, then the season unless <paramref name="marksSeason"/> is false.
    /// The one place a sync from trackers saves user data; applying a preview from trackers marks through it too.
    /// </summary>
    internal void Mark(User user, Season season, IReadOnlyList<Episode> episodes, DateTime playedDate, bool marksSeason) =>
        new PlayedMarker(_userDataManager, _logger).Mark(user, season, episodes, playedDate, marksSeason);

    /// <summary>
    /// Get a list of metadata IDs from our external APIs.
    /// </summary>
    /// <param name="animeList">List of anime you want to get the metadata IDs of.</param>
    /// <returns>List of metadata IDs.</returns>
    private async Task<(List<SyncAnimeMetadata> Found, List<InboundUnresolved> Unresolved)> GetMetadataIdsFromAnime(List<Anime> animeList, OperationContext operation) {
        List<SyncAnimeMetadata> animeIdProgress = new List<SyncAnimeMetadata>();
        var unresolved = new List<InboundUnresolved>();
        for (var i = 0; i < animeList.Count; i++) {
            operation.ReportProgress(i, animeList.Count);
            Anime entry = animeList[i];
            _logger.LogDebug("({Provider}) Looking up IDs for tracker entry {EntryId} ({Title})", _apiName, entry.Id, entry.Title);
            var ids = await _arm.GetIds(animeList[i].Id, ArmClient.MapFromApiName(_apiName));
            if (ids?.AniDb == null) {
                _logger.LogInformation("({Provider}) {Title} maps to no Jellyfin season; no AniDB ID is known for tracker entry {EntryId}", _apiName, animeList[i].Title, animeList[i].Id);
                unresolved.Add(new InboundUnresolved(animeList[i], "no-anidb-id"));
                continue;
            }

            AnimeListHelpers.AnimeListXml? animeListXml = await _animeList.Read();
            AnimeListHelpers.AnimeListAnime? season = AnimeListHelpers.GetAniDbSeason(ids.AniDb.Value, animeListXml);
            if (season == null || (!int.TryParse(season.Defaulttvdbseason, out var seasonNumber) && season.Defaulttvdbseason != "a")) {
                _logger.LogInformation("({Provider}) {Title} maps to no Jellyfin season; the anime list has no season for AniDB ID {AniDbId}", _apiName, animeList[i].Title, ids.AniDb.Value);
                unresolved.Add(new InboundUnresolved(animeList[i], "no-season"));
                await Sleep(operation.CancellationToken);
                continue;
            }

            int episodesWatched;
            if (entry.MyListStatus is { NumEpisodesWatched: not 0 } listStatus) {
                episodesWatched = int.TryParse(season.Episodeoffset, out int offset) ? listStatus.NumEpisodesWatched + offset : listStatus.NumEpisodesWatched;
            } else {
                episodesWatched = -1;
            }

            var syncAnimeMetadata = new SyncAnimeMetadata {
                ids = ids,
                episodesWatched = episodesWatched,
                season = seasonNumber,
                entry = animeList[i]
            };
            if (DateTime.TryParse(animeList[i].MyListStatus?.FinishDate, out DateTime finishDate)) {
                syncAnimeMetadata.completedAt = finishDate;
            }

            animeIdProgress.Add(syncAnimeMetadata);
            _logger.LogDebug("({Provider}) Found IDs for {Title}: AniDB ID {AniDbId}, season {SeasonNumber}", _apiName, animeList[i].Title, ids.AniDb.Value, seasonNumber);
            if (i != animeList.Count - 1) {
                await Sleep(operation.CancellationToken);
            }
        }

        operation.ReportProgress(animeList.Count, animeList.Count);
        return (animeIdProgress, unresolved);
    }

    /// <summary>
    /// Wait between tracker requests so we don't hammer the API, and stop here if the operation was cancelled.
    /// </summary>
    private async Task Sleep(CancellationToken cancellationToken) {
        _logger.LogDebug("Waiting {DelayMs} ms before the next tracker entry", _apiTimeOutLength);
        long started = Stopwatch.GetTimestamp();
        await _delayer.Delay(TimeSpan.FromMilliseconds(_apiTimeOutLength));
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Waited {ElapsedMs} ms before the next tracker entry", LogText.ElapsedMs(started));
        cancellationToken.ThrowIfCancellationRequested();
    }


    public class SyncAnimeMetadata {
        public ArmClient.OfflineDatabaseResponse? ids { get; set; }
        public int episodesWatched { get; set; }
        public int season { get; set; }
        public DateTime? completedAt { get; set; }
        public required Anime entry { get; set; }
    }
}
