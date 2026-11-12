#nullable disable
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
    internal Func<ApiName, UserConfig, ITrackerListSource> ListSourceOverride { get; set; }

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
            _logger.LogWarning("(Sync) Skipping sync from the tracker because AniDoki storage is unavailable: {StorageError}", storageError);
            operation.Result = OperationState.Failed;
            operation.Message = "AniDoki storage is unavailable, so nothing was imported.";
            return;
        }

        InboundPlanResult plan = await Plan(Guid.Parse(userId), operation);
        if (plan == null) {
            _logger.LogWarning("(Sync) No anime found by provider; please make sure user is authenticated with this provider and the authenticated users watch list is populated");
            operation.Result = OperationState.NoChange;
            operation.Message = "The tracker returned no anime for the selected statuses. Check that the account is linked.";
            return;
        }

        User user = _userManager.GetUserById(Guid.Parse(userId));
        foreach (InboundSeasonPlan season in plan.Seasons)
            Apply(user, season, userId);
        operation.Result = OperationState.Succeeded;
        operation.Message = plan.EntryCount == 1 ? "Checked 1 tracker entry." : $"Checked {plan.EntryCount} tracker entries.";
    }

    /// <summary>
    /// Get the providers anime list.
    /// </summary>
    /// <param name="userId">ID of the user to get the anime list of.</param>
    /// <returns>Users' provider anime list.</returns>
    private async Task<List<Anime>> GetAnimeList(string userId) {
        UserConfig config = Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId));
        ITrackerListSource source = ListSourceOverride?.Invoke(_apiName, config) ?? _apiName switch {
            ApiName.Mal => new MalTrackerClient(new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            ApiName.AniList => new AniListTrackerClient(new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            ApiName.Kitsu => new KitsuTrackerClient(new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, config)),
            _ => null
        };
        if (source == null)
            return null;

        MalApiCalls.User user = new MalApiCalls.User();
        // MAL's list needs no user ID; AniList and Kitsu do.
        if (_apiName is ApiName.AniList or ApiName.Kitsu) {
            user = await source.GetUser();
            if (user == null || user.Id == 0) {
                _logger.LogError("(Sync) Could not retrieve user information. Cannot proceed");
                return null;
            }
        }

        switch (_status) {
            case SyncHelper.Status.Completed:
                return await source.GetAnimeList(Status.Completed, user?.Id);
            case SyncHelper.Status.Watching:
                return await source.GetAnimeList(Status.Watching, user?.Id);
            case SyncHelper.Status.Both:
                List<Anime> completed = await source.GetAnimeList(Status.Completed, user?.Id);
                List<Anime> watching = await source.GetAnimeList(Status.Watching, user?.Id);
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
    internal async Task<InboundPlanResult> Plan(Guid userId, OperationContext operation) {
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
            _logger.LogError("(Sync) User with ID of {UserId} not found", userId);
            return plans;
        }
        foreach (BaseItem baseItem in userLibrary) {
            if (SyncHelper.MediaShouldBeIgnored(baseItem)) {
                _logger.LogDebug("(Sync) Ignoring {BaseItemName}; ignore tag detected", baseItem.Name);
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
                    _logger.LogError("(Sync) Could not retrieve necessary provider information. Skipping");
                }
            }
        }
        return plans;
    }

    private void Apply(User user, InboundSeasonPlan plan, string userId) {
        if (plan.SeasonIgnored) {
            _logger.LogDebug("(Sync) Ignoring {SeasonName}; ignore tag detected", plan.Season.Name);
            return;
        }

        foreach (Episode ignored in plan.IgnoredEpisodes)
            _logger.LogDebug("(Sync) Ignoring {ItemName}; ignore tag detected", ignored.Name);
        foreach (Episode episode in plan.Episodes) {
            _logger.LogInformation("(Sync) Setting {SeriesName} season {SeasonIndexNumber} episode {EpisodeIndexNumber} for user {UserId} as played...", episode.Series.Name, episode.Season.IndexNumber, episode.IndexNumber, userId);
            _userDataManager.SaveUserData(user, episode, SetUserData(user, episode, plan.PlayedDate), UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }

        if (!plan.MarksSeason) {
            _logger.LogDebug("(Sync) Not marking season {SeasonName} as played; one or more episodes carry the ignore tag", plan.Season.Name);
        } else {
            // this could fail because show has ovas, hard to detect. todo warn users
            _userDataManager.SaveUserData(user, plan.Season, SetUserData(user, plan.Season, plan.PlayedDate), UserDataSaveReason.UpdateUserData, CancellationToken.None);
            _logger.LogInformation("(Sync) Saved");
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
        var userItemData = _userDataManager.GetUserData(user, itemToBeUpdated);
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
            _logger.LogDebug("(Sync) Fetching IDs for anime with an ID of {AnimeListId}...", animeList[i].Id);
            var ids = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), _logger, animeList[i].Id, AnimeOfflineDatabaseHelpers.MapFromApiName(_apiName));
            if (ids?.AniDb == null) {
                _logger.LogError("(Sync) Could not retrieve AniDb ID; skipping item...");
                unresolved.Add(new InboundUnresolved(animeList[i], "no-anidb-id"));
                continue;
            }

            AnimeListHelpers.AnimeListXml animeListXml = await AnimeListHelpers.GetAnimeListFileContents(_logger, _loggerFactory, _httpClientFactory, _applicationPaths);
            AnimeListHelpers.AnimeListAnime season = AnimeListHelpers.GetAniDbSeason(ids.AniDb.Value, animeListXml);
            if (season == null || (!int.TryParse(season.Defaulttvdbseason, out var seasonNumber) && season.Defaulttvdbseason != "a")) {
                _logger.LogError("(Sync) Could not retrieve season number; skipping item...");
                unresolved.Add(new InboundUnresolved(animeList[i], "no-season"));
                await Sleep(operation.CancellationToken);
                continue;
            }

            int episodesWatched;
            if ((animeList[i].MyListStatus != null && animeList[i].MyListStatus.NumEpisodesWatched != 0)) {
                episodesWatched = int.TryParse(season.Episodeoffset, out int offset) ? animeList[i].MyListStatus.NumEpisodesWatched + offset : animeList[i].MyListStatus.NumEpisodesWatched;
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
            _logger.LogInformation("(Sync) Fetched");
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
        _logger.LogDebug("(Sync) Waiting 2 seconds before proceeding...");
        await _delayer.Delay(TimeSpan.FromMilliseconds(_apiTimeOutLength));
        cancellationToken.ThrowIfCancellationRequested();
    }


    public class SyncAnimeMetadata {
        public AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids { get; set; }
        public int episodesWatched { get; set; }
        public int season { get; set; }
        public DateTime? completedAt { get; set; }
        public Anime entry { get; set; }
    }
}
