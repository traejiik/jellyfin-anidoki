using System;
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
using MediaBrowser.Common.Configuration;
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
    private readonly IApplicationPaths _applicationPaths;
    private readonly IUserDataManager _userDataManager;
    private readonly IMemoryCache _memoryCache;
    private readonly IAsyncDelayer _delayer;
    private readonly ApiName _apiName;
    private readonly SyncHelper.Status _status;
    private int _apiTimeOutLength = 2000;

    /// <summary>Tests set this to substitute an in-memory tracker list; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig?, ITrackerListSource>? ListSourceOverride { get; set; }

    public Sync(IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServerApplicationHost serverApplicationHost,
        IHttpContextAccessor httpContextAccessor,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        IUserDataManager userDataManager,
        IMemoryCache memoryCache,
        IAsyncDelayer delayer,
        ApiName apiName,
        SyncHelper.Status status) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
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
            foreach (InboundSeasonPlan season in plan.Seasons)
                Apply(user, season);
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
            case SyncHelper.Status.Completed:
                return await source.GetAnimeList(Status.Completed, user?.Id);
            case SyncHelper.Status.Watching:
                return await source.GetAnimeList(Status.Watching, user?.Id);
            case SyncHelper.Status.Both:
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
        var completedList = await GetAnimeList(userId.ToString());
        if (completedList == null)
            return null;

        var (found, unresolved) = await GetMetadataIdsFromAnime(completedList, operation);
        return new InboundPlanResult(completedList.Count, await PlanLibrary(userId, found), unresolved);
    }

    private async Task<List<InboundSeasonPlan>> PlanLibrary(Guid userId, List<SyncAnimeMetadata> convertedWatchList) {
        var plans = new List<InboundSeasonPlan>();
        var userLibrary = SyncHelper.GetUsersJellyfinLibrary(userId, _userManager, _libraryManager);
        var user = _userManager.GetUserById(userId);
        if (user == null) {
            _logger.LogWarning("User {UserId} was not found in Jellyfin; nothing was planned", userId);
            return plans;
        }
        foreach (BaseItem baseItem in userLibrary) {
            if (SyncHelper.MediaShouldBeIgnored(baseItem)) {
                _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", baseItem.Name);
            } else if (baseItem is Series series) {
                (AnimeOfflineDatabaseHelpers.Source? source, int? providerId) = SyncHelper.GetSeriesProviderId(series);

                if (providerId != null && providerId != 0 && source != null) {
                    var seasons = await SyncHelper.GetJellyfinSeasons(convertedWatchList, providerId.Value, series, source.Value,
                        _logger, _loggerFactory, _httpClientFactory, _applicationPaths);
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
    internal void Mark(User user, Season season, IReadOnlyList<Episode> episodes, DateTime playedDate, bool marksSeason) {
        foreach (Episode episode in episodes) {
            _logger.LogDebug("Marking {SeriesName} season {SeasonNumber} episode {EpisodeNumber} as played for user {UserId}", episode.Series.Name, episode.Season.IndexNumber, episode.IndexNumber, user.Id);
            _userDataManager.SaveUserData(user, episode, SetUserData(user, episode, playedDate), UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }

        if (!marksSeason) {
            _logger.LogDebug("Not marking {ItemName} itself as played; one or more of its episodes carry the ignore tag", season.Name);
        } else {
            // this could fail because show has ovas, hard to detect. todo warn users
            _userDataManager.SaveUserData(user, season, SetUserData(user, season, playedDate), UserDataSaveReason.UpdateUserData, CancellationToken.None);
            _logger.LogDebug("Marked {ItemName} itself as played", season.Name);
        }
    }

    /// <summary>
    /// Save the select users items.
    /// </summary>
    /// <param name="user">User you want to save the items for.</param>
    /// <param name="itemToBeUpdated">Items to be saved.</param>
    /// <param name="completedDate">Played date.</param>
    /// <returns>User item data.</returns>
    private UserItemData SetUserData(User user, BaseItem itemToBeUpdated, DateTime? completedDate) {
        // Jellyfin creates the user data row when it reads one, so a null here is unexpected; it stays an exception, which the operation reports.
        var userItemData = _userDataManager.GetUserData(user, itemToBeUpdated)!;
        userItemData.Played = true;
        if (completedDate != null) {
            userItemData.LastPlayedDate = completedDate.Value;
        }

        return userItemData;
    }

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
            var ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), _logger, animeList[i].Id, AnimeOfflineDatabaseHelpers.MapFromApiName(_apiName));
            if (ids?.AniDb == null) {
                _logger.LogInformation("({Provider}) {Title} maps to no Jellyfin season; no AniDB ID is known for tracker entry {EntryId}", _apiName, animeList[i].Title, animeList[i].Id);
                unresolved.Add(new InboundUnresolved(animeList[i], "no-anidb-id"));
                continue;
            }

            AnimeListHelpers.AnimeListXml? animeListXml = await AnimeListHelpers.GetAnimeListFileContents(_logger, _loggerFactory, _httpClientFactory, _applicationPaths);
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
        await _delayer.Delay(TimeSpan.FromMilliseconds(_apiTimeOutLength));
        cancellationToken.ThrowIfCancellationRequested();
    }


    public class SyncAnimeMetadata {
        public AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids { get; set; }
        public int episodesWatched { get; set; }
        public int season { get; set; }
        public DateTime? completedAt { get; set; }
        public required Anime entry { get; set; }
    }
}
