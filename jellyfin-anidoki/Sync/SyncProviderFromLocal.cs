using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller;
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
    ArmClient arm,
    AnimeListSource animeList,
    IMemoryCache memoryCache,
    IAsyncDelayer delayer,
    string userId,
    ISeasonWatchState? watchState = null) {
    private readonly Guid _userId = Guid.Parse(userId);
    private readonly ILogger<SyncProviderFromLocal> _logger = loggerFactory.CreateLogger<SyncProviderFromLocal>();

    /// <summary>Passed to the updater's <see cref="SyncPipeline.ClientOverride"/>; tests substitute a tracker.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; set; }

    /// <summary>The one linked tracker to sync, or null for every linked tracker.</summary>
    internal ApiName? OnlyProvider { get; set; }

    /// <summary>
    /// Sends the latest watched episode of every season to the user's trackers. Runs inside an operation, which
    /// already holds the user's sync gate.
    /// </summary>
    public async Task SyncFromLocal(OperationContext operation) {
        _logger.LogDebug("Starting a sync to trackers for user {UserId}", _userId);
        await SyncEpisodes(LatestWatchedEpisodes.For(_userId, userManager, libraryManager, _logger), operation);
    }

    internal async Task SyncEpisodes(IReadOnlyList<Episode> targets, OperationContext operation) {
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, arm, animeList, memoryCache, delayer) {
            ClientOverride = ClientOverride,
            WatchState = watchState,
            OnlyProvider = OnlyProvider
        };
        var collector = new PlaybackOutcomeCollector(operation.Record);
        operation.ReportProgress(0, targets.Count);
        for (int i = 0; i < targets.Count; i++) {
            if (operation.CancellationToken.IsCancellationRequested) {
                operation.Message = $"Cancelled after {i} of {Seasons(targets.Count)}. Updates already sent were kept.";
                operation.CancellationToken.ThrowIfCancellationRequested();
            }
            Episode episode = targets[i];
            if (SyncHelper.MediaShouldBeIgnored(episode)) {
                _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", episode.Name);
            } else {
                long seasonStarted = Stopwatch.GetTimestamp();
                // A season that throws may have reached a tracker, so it is paced like one that did.
                bool asked = true;
                try {
                    asked = await pipeline.Run(episode, _userId, true, collector, SyncUpdateSource.ManualSync, operation.CancellationToken);
                } catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) {
                    operation.Message = $"Cancelled after {i} of {Seasons(targets.Count)}. Updates already sent were kept.";
                    throw;
                } catch (Exception e) {
                    _logger.LogError(e, "Could not sync {SeriesName} season {SeasonNumber}", episode.SeriesName, episode.ParentIndexNumber);
                }
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Synced {SeriesName} season {SeasonNumber} in {ElapsedMs} ms", episode.SeriesName, episode.ParentIndexNumber, LogText.ElapsedMs(seasonStarted));
                // Only a season that asked a tracker counts toward its rate limits.
                if (asked && i < targets.Count - 1) {
                    _logger.LogDebug("Waiting 2 seconds before the next season");
                    long waitStarted = Stopwatch.GetTimestamp();
                    await delayer.Delay(TimeSpan.FromSeconds(2));
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Waited {ElapsedMs} ms before the next season", LogText.ElapsedMs(waitStarted));
                }
            }
            operation.ReportProgress(i + 1, targets.Count);
        }
        operation.Message = $"Checked {Seasons(targets.Count)} with watched episodes.";
    }

    private static string Seasons(int count) => count == 1 ? "1 season" : $"{count} seasons";
}
