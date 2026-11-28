using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Logging;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Settings;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// Listens to Jellyfin's playback events. Start and progress reports feed each user's watch (<see cref="WatchTracker"/>).
/// A stop that Jellyfin reports as played to completion, and whose watch reached Jellyfin's resume percentage
/// (<see cref="WatchEvaluator"/>), starts one Playback operation per user, once per play session (see <see cref="CompletionLedger"/>).
/// A user with a completion percentage (D3) gets that operation during playback instead, when the watch passes it;
/// the stop that follows is then a duplicate.
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
    CompletionLedger ledger,
    WatchTracker watches,
    ManualMarkStore? manualMarks = null,
    DiscordNotificationDispatcher? discord = null,
    ISeasonWatchState? watchState = null) : IHostedService {
    private readonly ILogger<PlaybackListener> _logger = loggerFactory.CreateLogger<PlaybackListener>();
    private readonly IAsyncDelayer _delayer = new Delayer();
    private readonly ProgressLogGate _progressLines = new();
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
        Observe(e, false);
    }

    private void PlaybackProgressed(object? sender, PlaybackProgressEventArgs e) {
        if (e.Item != null && _logger.IsEnabled(LogLevel.Debug)
            && _progressLines.ShouldLog(ProgressSession(e), Seconds(e.PlaybackPositionTicks), e.IsPaused))
            _logger.LogDebug("Playback of {ItemName} is at {PositionSeconds} s, paused {IsPaused}", e.Item.Name, Seconds(e.PlaybackPositionTicks), e.IsPaused);
        Observe(e, true);
    }

    /// <summary>Feeds a start or progress report to each user's watch. A failure here never reaches Jellyfin.</summary>
    private void Observe(PlaybackProgressEventArgs e, bool isProgress) {
        try {
            if (e.Item is not BaseItem item || WatchCycle(e) is not string cycle)
                return;
            // The ledger keys the stop by its play session, so without one an earlier update could not be told from the stop.
            bool hasSession = !string.IsNullOrEmpty(e.PlaySessionId);
            foreach (User user in e.Users) {
                if (watches.Observe(user, item, cycle, e.PlaybackPositionTicks, e.IsPaused, isProgress, hasSession ? ChosenThreshold(user.Id) : null))
                    Crossed(e, item, user.Id, cycle);
            }
        } catch (Exception exception) {
            _logger.LogDebug(exception, "Could not measure the playback report for {ItemName}", e.Item?.Name);
        }
    }

    private int? ChosenThreshold(Guid userId) {
        var configuration = Plugin.Instance?.PluginConfiguration;
        int? chosen = configuration?.UserConfig?.FirstOrDefault(config => config.UserId == userId)?.TrackerThresholdPercent;
        return TrackerThreshold.ForUser(chosen, configuration?.DefaultTrackerThresholdPercent, watches.MaxResumePct);
    }

    /// <summary>The watch passed the user's completion percentage: the trackers are updated now, while it plays.</summary>
    private void Crossed(PlaybackProgressEventArgs e, BaseItem item, Guid userId, string cycle) {
        if (SyncHelper.MediaShouldBeIgnored(item)) {
            _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", item.Name);
            return;
        }
        _logger.LogDebug("Counted {ItemName} as watched during playback, at the user's completion percentage", item.Name);
        Start(item, userId, cycle, CompletionTrigger.Threshold, PlaybackNotificationContext.Capture(item, e.Session), Stopwatch.GetTimestamp());
    }

    private void Start(BaseItem item, Guid userId, string cycle, CompletionTrigger trigger, PlaybackNotificationContext context, long counted) {
        Guid operationId = Guid.NewGuid();
        if (!Admit(userId, item, cycle, trigger, operationId))
            return;
        var handle = runner.Enqueue(userId, OperationKind.Playback, OperationSources.Describe(item),
            operation => RunPlayback(item, userId, context, counted, operation), operationId, retry: new RetrySubject(item.Id, OperationKind.Playback));
        if (handle == null)
            _logger.LogWarning("Playback update for {ItemName} was not started because AniDoki storage is unavailable", item.Name);
    }

    private void PlaybackStopped(object? sender, PlaybackStopEventArgs e) {
        _progressLines.Forget(ProgressSession(e));
        try {
            BaseItem? item = e.Item;
            if (item == null)
                return;
            // Every stop ends its watches, finished or not.
            var verdicts = new List<(User User, WatchVerdict Verdict)>(e.Users.Count);
            foreach (User user in e.Users)
                verdicts.Add((user, Judge(user, item, e)));
            // Unfinished playback never syncs (SyncPipeline requires completion), so it starts no operation.
            if (!e.PlayedToCompletion)
                return;
            if (SyncHelper.MediaShouldBeIgnored(item)) {
                _logger.LogDebug("Ignoring {ItemName}; it carries the ignore tag", item.Name);
                return;
            }
            var context = PlaybackNotificationContext.Capture(item, e.Session);
            long stopped = Stopwatch.GetTimestamp();
            // Without a play session no two reports can be told apart, so each counts on its own.
            string cycle = string.IsNullOrEmpty(e.PlaySessionId) ? Guid.NewGuid().ToString("N") : e.PlaySessionId;
            foreach (var (user, verdict) in verdicts) {
                if (Counts(item, verdict))
                    Start(item, user.Id, cycle, CompletionTrigger.Stop, context, stopped);
            }
        } catch (Exception exception) {
            _logger.LogError(exception, "Could not start the playback update");
        }
    }

    /// <summary>The watch's verdict. If measuring fails, Jellyfin's own completion decides, as it did before measuring.</summary>
    private WatchVerdict Judge(User user, BaseItem item, PlaybackStopEventArgs e) {
        try {
            return watches.Stop(user, item, WatchCycle(e), e.PlaybackPositionTicks, e.PlayedToCompletion);
        } catch (Exception exception) {
            _logger.LogDebug(exception, "Could not measure the playback of {ItemName}; Jellyfin's completion decides", item.Name);
            return new WatchVerdict(e.PlayedToCompletion, WatchRule.JellyfinNoPosition, 0, 0, 0);
        }
    }

    /// <summary>Logs a stop that does not count, and how a counted stop was measured when it could not be measured fully.</summary>
    private bool Counts(BaseItem item, WatchVerdict verdict) {
        if (!verdict.Counts) {
            _logger.LogInformation("Did not count {ItemName} as watched; playback reached {CreditedSeconds} s of the {RequiredSeconds} s needed",
                item.Name, Seconds(verdict.CreditedTicks), Seconds(verdict.RequiredTicks));
            return false;
        }
        if (verdict.Rule is WatchRule.StopPosition or WatchRule.JellyfinNoPosition)
            _logger.LogDebug("Measured {ItemName} from the stop position; the client sent {Count} progress reports", item.Name, verdict.ProgressReports);
        return true;
    }

    /// <summary>The play session that keys a watch, or the device session when the client sent none.</summary>
    private static string? WatchCycle(PlaybackProgressEventArgs e) =>
        !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId : e.Session?.Id is { Length: > 0 } session ? session : null;

    /// <summary>
    /// Asks the ledger; false drops a duplicate, such as the stop after the completion percentage was passed. Storage
    /// errors let it through, and enqueuing then reports them.
    /// </summary>
    private bool Admit(Guid userId, BaseItem item, string cycle, CompletionTrigger trigger, Guid operationId) {
        try {
            if (ledger.TryRecord(userId, item.Id, cycle, trigger, operationId))
                return true;
            if (trigger == CompletionTrigger.Stop)
                _logger.LogDebug("Dropped a duplicate stop for {ItemName}, because this watch is already recorded", item.Name);
            else
                _logger.LogDebug("Dropped the update at the completion percentage for {ItemName}, because this watch is already recorded", item.Name);
            return false;
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogDebug("Could not check the completion ledger for {ItemName}: {ErrorMessage}", item.Name, error.Message);
            return true;
        }
    }

    /// <param name="counted">When the watch counted: the stop, or the report that passed the completion percentage.</param>
    private async Task RunPlayback(BaseItem item, Guid userId, PlaybackNotificationContext context, long counted, OperationContext operation) {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Started the playback update for {ItemName} {ElapsedMs} ms after the watch counted", item.Name, LogText.ElapsedMs(counted));
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
                _logger.LogDebug("Finished the playback update for {ItemName} {ElapsedMs} ms after the watch counted; a toast is published only for a confirmed change", item.Name, LogText.ElapsedMs(counted));
        }
    }

    /// <summary>The play session, or the item on the device when the client sent none.</summary>
    private static string ProgressSession(PlaybackProgressEventArgs e) =>
        string.IsNullOrEmpty(e.PlaySessionId) ? $"{e.Item?.Id:N}/{e.DeviceId}" : e.PlaySessionId;

    private static long Seconds(long? ticks) => (ticks ?? 0) / TimeSpan.TicksPerSecond;
}
