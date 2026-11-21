using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>One watched mark from a client without the confirmation prompt.</summary>
public readonly record struct ToggleMark(Guid UserId, Guid? SeasonId, Video Item);

/// <summary>
/// Batches watched marks from clients without the confirmation prompt and runs one operation per user and season,
/// for the highest marked episode.
/// </summary>
public sealed class WatchedToggleTrigger(
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IMemoryCache memoryCache,
    IHttpContextAccessor httpContextAccessor,
    IServerApplicationHost serverApplicationHost,
    IHttpClientFactory httpClientFactory,
    ArmClient arm,
    AnimeListSource animeList,
    IAsyncDelayer delayer,
    OperationRunner runner,
    CompletionLedger ledger,
    DiscordNotificationDispatcher? discord = null,
    ISeasonWatchState? watchState = null) {
    /// <summary>How long a mark waits for others from the same mass update before its batch runs.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    private readonly ILogger<WatchedToggleTrigger> _logger = loggerFactory.CreateLogger<WatchedToggleTrigger>();
    private readonly List<ToggleMark> _pending = [];
    private readonly object _lock = new();
    private Task? _drain;

    /// <summary>Queues a mark and starts the drain loop when it is idle. Returns at once, so the client's save is not held up.</summary>
    public void Add(ToggleMark mark) {
        lock (_lock) {
            _pending.Add(mark);
            if (_drain == null || _drain.IsCompleted)
                _drain = Drain();
        }
    }

    private async Task Drain() {
        await delayer.Delay(CoalesceWindow);
        while (true) {
            List<ToggleMark> batch;
            lock (_lock) {
                if (_pending.Count == 0)
                    return;
                ToggleMark first = _pending[0];
                batch = _pending.Where(mark => mark.SeasonId == first.SeasonId && mark.UserId == first.UserId).ToList();
                // Removed before running, so a batch that fails never holds up the ones behind it.
                _pending.RemoveAll(batch.Contains);
            }

            try {
                await Run(batch);
            } catch (Exception error) {
                _logger.LogError(error, "Could not update trackers for a watched mark");
            }
        }
    }

    private async Task Run(List<ToggleMark> batch) {
        ToggleMark first = batch[0];
        Guid userId = first.UserId;
        var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(config => config.UserId == userId);
        if (userConfig == null || !LibraryFilter.Includes(userConfig, libraryManager, animeList.Index, _logger, first.Item))
            return;
        List<ToggleMark> kept = batch.Where(mark => !SyncHelper.MediaShouldBeIgnored(mark.Item)).ToList();
        if (kept.Count == 0) {
            _logger.LogDebug("Ignoring {ItemName}; every paired episode carries the ignore tag", first.Item.Name);
            return;
        }

        Video item = kept.MaxBy(mark => mark.Item.IndexNumber).Item;
        Guid operationId = Guid.NewGuid();
        if (!Admit(userId, item, operationId))
            return;
        var handle = runner.Enqueue(userId, OperationKind.WatchedToggle, OperationSources.Describe(item), operation => Update(item, userId, operation), operationId);
        if (handle == null) {
            _logger.LogWarning("Watched-mark update for {ItemName} was not started because AniDoki storage is unavailable", item.Name);
            return;
        }
        // One batch at a time, so a mass update reaches the trackers in order.
        await handle.Completion;
    }

    /// <summary>
    /// Asks the ledger; false drops a mark made within <see cref="CompletionLedger.DedupeWindow"/> of a completion of the
    /// same item. Storage errors let the mark through, and enqueuing then reports them.
    /// </summary>
    private bool Admit(Guid userId, Video item, Guid operationId) {
        try {
            if (ledger.TryRecord(userId, item.Id, Guid.NewGuid().ToString("N"), CompletionTrigger.Toggle, operationId, CompletionLedger.DedupeWindow))
                return true;
            _logger.LogDebug("Dropped the watched mark for {ItemName}, because a recent watch already updated trackers", item.Name);
            return false;
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogDebug("Could not check the completion ledger for {ItemName}: {ErrorMessage}", item.Name, error.Message);
            return true;
        }
    }

    private async Task Update(Video item, Guid userId, OperationContext operation) {
        var collector = new PlaybackOutcomeCollector(operation.Record);
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, arm, animeList, memoryCache, delayer) {
            WatchState = watchState
        };
        try {
            await pipeline.Run(item, userId, true, collector, SyncUpdateSource.LegacyManual, operation.CancellationToken);
        } finally {
            discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, SyncNotificationTrigger.LegacyManual, collector));
        }
    }
}
