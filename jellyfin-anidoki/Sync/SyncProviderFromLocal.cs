#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

public class SyncProviderFromLocal(
    IUserManager userManager,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths,
    IMemoryCache memoryCache,
    IAsyncDelayer delayer,
    string userId) {
    private readonly Guid _userId = Guid.Parse(userId);
    private readonly ILogger<SyncProviderFromLocal> _logger = loggerFactory.CreateLogger<SyncProviderFromLocal>();

    public async Task SyncFromLocal() {
        using var lease = await SyncExecutionGate.Shared.Enter(_userId);
        var jellyfinLibrary = SyncHelper.GetUsersJellyfinLibrary(_userId, userManager, libraryManager);
        List<Series> userSeriesList = jellyfinLibrary.OfType<Series>().Select(baseItem => baseItem).ToList();
        await GetSeasonDetails(userSeriesList);
    }

    private async Task GetSeasonDetails(List<Series> userSeriesList) {
        _logger.LogInformation("(Sync) Starting sync to provider from local process");
        UpdateProviderStatus updateProviderStatus = new UpdateProviderStatus(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, applicationPaths, memoryCache, delayer);

        foreach (Series series in userSeriesList) {
            _logger.LogDebug("(Sync) Retrieved {SeriesName}'s seasons latest watched episode and when it was watched...", series.Name);
            var toMarkAsCompleted = GetMaxEpisodeAndCompletedTime(series);
            if (toMarkAsCompleted != null) {
                foreach (Episode episodeDateTime in toMarkAsCompleted) {
                    if (episodeDateTime != null) {
                        if (SyncHelper.MediaShouldBeIgnored(episodeDateTime)) {
                            _logger.LogDebug("(Sync) Ignoring {ItemName}; ignore tag detected", episodeDateTime.Name);
                            continue;
                        }
                        try {
                            await updateProviderStatus.Update(episodeDateTime, _userId, true);
                        } catch (Exception e) {
                            _logger.LogError("(Sync) Could not sync item; error: {ErrorMessage}", e.Message);
                            continue;
                        }

                        _logger.LogInformation("(Sync) Waiting 2 seconds before continuing...");
                        Thread.Sleep(2000);
                    } else {
                        _logger.LogError("(Sync) Could not get users Jellyfin data for this season");
                    }
                }
            } else {
                _logger.LogError("(Sync) User with ID of {UserId} not found", _userId);
            }
        }
    }

    private List<Episode> GetMaxEpisodeAndCompletedTime(Series series) {
        List<Episode> returnDictionary = new List<Episode>();

        _logger.LogDebug("(Sync) Getting {SeriesName} seasons latest watched episode", series.Name);
        var seasons = series.Children.OfType<Season>().Select(baseItem => baseItem).ToList();
        _logger.LogDebug("(Sync) Series {SeriesName} contains {SeasonCount} seasons", series.Name, seasons.Count);
        User user = userManager.GetUserById(_userId);
        if (user == null)
            return null;
        foreach (Season season in seasons) {
            _logger.LogDebug("(Sync) Getting user data for {SeasonName} of {SeriesName}...", season.Name, series.Name);
            if (season.IndexNumber == null) {
                _logger.LogError("(Sync) Season index number is null. Skipping...");
                continue;
            }

            IEnumerable<Episode> episodes = season.GetEpisodes(user, new DtoOptions(), false).OfType<Episode>().ToArray();
            if (!episodes.Any()) {
                _logger.LogDebug("(Sync) No (user visible) episodes found for {SeasonName} of {SeriesName}", season.Name, series.Name);
            }
            _logger.LogDebug("(Sync) Season contains {EpisodeCount} episodes", season.Children.OfType<Episode>().Count());
            Episode latestWatchedEpisode;

            try {
                latestWatchedEpisode = episodes.Where(episode => episode.UserData.Any(userData => userData.UserId == user.Id && userData.Played)).MaxBy(episode => episode.IndexNumber);
            } catch (Exception e) {
                _logger.LogError("(Sync) Could not get user episodes watched for {SeasonName}; error: {ErrorMessage}", season.Name, e.Message);
                continue;
            }

            if (latestWatchedEpisode == null) {
                _logger.LogDebug("(Sync) No episodes watched for {SeasonName}", season.Name);
                continue;
            }
            _logger.LogDebug("(Sync) The latest watched episode for this user of this season is {EpisodeNumber}", latestWatchedEpisode.IndexNumber);
            returnDictionary.Add(latestWatchedEpisode);
        }

        _logger.LogDebug("(Sync) Found {SeasonCount} seasons that contain user data", returnDictionary.Count);
        return returnDictionary;
    }
}
