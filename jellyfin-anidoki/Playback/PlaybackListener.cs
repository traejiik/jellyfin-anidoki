using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// Listens to Jellyfin's playback events. A stop that Jellyfin reports as played to completion starts one Playback
/// operation per user. Start and progress reports are only logged for now; the watch evaluator reads them later.
/// </summary>
public sealed class PlaybackListener(
    ISessionManager sessionManager,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ILibraryManager libraryManager,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    ArmClient arm,
    AnimeListSource animeList,
    IMemoryCache memoryCache,
    NotificationEventStore notifications,
    OperationRunner runner,
    ManualMarkStore? manualMarks = null,
    DiscordNotificationDispatcher? discord = null,
    ISeasonWatchState? watchState = null) : IHostedService {
    private readonly ILogger<PlaybackListener> _logger = loggerFactory.CreateLogger<PlaybackListener>();
    private readonly IAsyncDelayer _delayer = new Delayer();
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_started) {
            sessionManager.PlaybackStart += PlaybackStarted;
            sessionManager.PlaybackProgress += PlaybackProgressed;
            sessionManager.PlaybackStopped += PlaybackStopped;
        }
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        if (_started) {
            sessionManager.PlaybackStart -= PlaybackStarted;
            sessionManager.PlaybackProgress -= PlaybackProgressed;
            sessionManager.PlaybackStopped -= PlaybackStopped;
        }
        _started = false;
        return Task.CompletedTask;
    }

    private void PlaybackStarted(object? sender, PlaybackProgressEventArgs e) {
        if (e.Item != null && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Playback started for {ItemName} at {PositionSeconds} s", e.Item.Name, Seconds(e.PlaybackPositionTicks));
    }

    private void PlaybackProgressed(object? sender, PlaybackProgressEventArgs e) {
        if (e.Item != null && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Playback of {ItemName} is at {PositionSeconds} s, paused {IsPaused}", e.Item.Name, Seconds(e.PlaybackPositionTicks), e.IsPaused);
    }

    private void PlaybackStopped(object? sender, PlaybackStopEventArgs e) {
        try {
            // Unfinished playback never syncs (SyncPipeline requires completion), so it starts no operation.
            if (!e.PlayedToCompletion)
                return;
            BaseItem? item = e.Item;
            if (item == null)
                return;
            if (SyncHelper.MediaShouldBeIgnored(item)) {
                _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", item.Name);
                return;
            }
            var context = PlaybackNotificationContext.Capture(item, e.Session);
            long stopped = Stopwatch.GetTimestamp();
            foreach (User user in e.Users) {
                Guid userId = user.Id;
                var handle = runner.Enqueue(userId, OperationKind.Playback, OperationSources.Describe(item),
                    operation => RunPlayback(item, userId, context, stopped, operation));
                if (handle == null)
                    _logger.LogWarning("Playback update for {ItemName} was not started because AniDoki storage is unavailable", item.Name);
            }
        } catch (Exception exception) {
            _logger.LogError(exception, "Could not start the playback update");
        }
    }

    private async Task RunPlayback(BaseItem item, Guid userId, PlaybackNotificationContext context, long stopped, OperationContext operation) {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Started the playback update for {ItemName} {ElapsedMs} ms after playback stopped", item.Name, LogText.ElapsedMs(stopped));
        var collector = new PlaybackOutcomeCollector(operation.Record);
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, arm, animeList, memoryCache, _delayer) {
            WatchState = watchState
        };
        try {
            await pipeline.Run(item, userId, true, collector, SyncUpdateSource.Playback, operation.CancellationToken);
        } finally {
            if (collector.HasConfirmedChange)
                manualMarks?.Supersede(userId, item.Id);
            discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, SyncNotificationTrigger.Playback, collector));
            PlaybackNotificationPublisher.Publish(collector, context, userId, notifications, _logger);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Finished the playback update for {ItemName} {ElapsedMs} ms after playback stopped; a toast is published only for a confirmed change", item.Name, LogText.ElapsedMs(stopped));
        }
    }

    private static long Seconds(long? ticks) => (ticks ?? 0) / TimeSpan.TicksPerSecond;
}
