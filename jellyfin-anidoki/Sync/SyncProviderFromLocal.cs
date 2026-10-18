#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
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

    /// <summary>
    /// Sends the latest watched episode of every season to the user's trackers. Runs inside an operation, which
    /// already holds the user's sync gate.
    /// </summary>
    public async Task SyncFromLocal(OperationContext operation) {
        _logger.LogInformation("(Sync) Starting sync to provider from local process");
        var jellyfinLibrary = SyncHelper.GetUsersJellyfinLibrary(_userId, userManager, libraryManager);
        var targets = new List<Episode>();
        foreach (Series series in jellyfinLibrary.OfType<Series>()) {
            _logger.LogDebug("(Sync) Retrieved {SeriesName}'s seasons latest watched episode and when it was watched...", series.Name);
            var latest = GetMaxEpisodeAndCompletedTime(series);
            if (latest == null) {
                _logger.LogError("(Sync) User with ID of {UserId} not found", _userId);
                continue;
            }
            targets.AddRange(latest);
        }
        await SyncEpisodes(targets, operation);
    }

    internal async Task SyncEpisodes(IReadOnlyList<Episode> targets, OperationContext operation) {
        var updateProviderStatus = new UpdateProviderStatus(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, applicationPaths, memoryCache, delayer);
        var collector = new PlaybackOutcomeCollector(operation.Record);
        operation.ReportProgress(0, targets.Count);
        for (int i = 0; i < targets.Count; i++) {
            if (operation.CancellationToken.IsCancellationRequested) {
                operation.Message = $"Cancelled after {i} of {Seasons(targets.Count)}. Updates already sent were kept.";
                operation.CancellationToken.ThrowIfCancellationRequested();
            }
            Episode episode = targets[i];
            if (SyncHelper.MediaShouldBeIgnored(episode)) {
                _logger.LogDebug("(Sync) Ignoring {ItemName}; ignore tag detected", episode.Name);
            } else {
                try {
                    // UpdateInsideGate, not Update: the operation already holds this user's gate.
                    await updateProviderStatus.UpdateInsideGate(episode, _userId, true, collector, SyncUpdateSource.Playback);
                } catch (Exception e) {
                    _logger.LogError("(Sync) Could not sync item; error: {ErrorMessage}", e.Message);
                }
                if (i < targets.Count - 1) {
                    _logger.LogDebug("(Sync) Waiting 2 seconds before continuing...");
                    await delayer.Delay(TimeSpan.FromSeconds(2));
                }
            }
            operation.ReportProgress(i + 1, targets.Count);
        }
        operation.Message = $"Checked {Seasons(targets.Count)} with watched episodes.";
    }

    private static string Seasons(int count) => count == 1 ? "1 season" : $"{count} seasons";

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
