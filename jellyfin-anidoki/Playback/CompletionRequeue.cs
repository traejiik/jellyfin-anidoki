using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// At startup, enqueues once more every completion whose operation the last restart interrupted. Re-running is safe:
/// the planner re-reads each tracker and finds nothing to change where the first run already wrote. Registered after
/// <see cref="OperationRunner"/>, which marks those operations interrupted when it starts.
/// </summary>
public sealed class CompletionRequeue(
    CompletionLedger ledger,
    OperationRunner runner,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ILibraryManager libraryManager,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    ArmClient arm,
    AnimeListSource animeList,
    IMemoryCache memoryCache,
    ManualMarkStore? manualMarks = null,
    DiscordNotificationDispatcher? discord = null,
    ISeasonWatchState? watchState = null) : IHostedService {
    private readonly ILogger<CompletionRequeue> _logger = loggerFactory.CreateLogger<CompletionRequeue>();
    private readonly IAsyncDelayer _delayer = new Delayer();

    /// <summary>Test seam: the tracker clients the requeued pipelines use.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; init; }

    public Task StartAsync(CancellationToken cancellationToken) {
        try {
            int count = 0;
            foreach (WatchCompletion completion in ledger.Interrupted()) {
                if (Requeue(completion))
                    count++;
            }
            if (count > 0)
                _logger.LogInformation("Requeued {Count} watch updates interrupted by the last restart", count);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogWarning("Could not requeue watch updates interrupted by the last restart: {ErrorMessage}", error.Message);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool Requeue(WatchCompletion completion) {
        BaseItem? item = libraryManager.GetItemById(completion.ItemId);
        if (item == null || SyncHelper.MediaShouldBeIgnored(item)) {
            // Marked anyway, so it is not looked at again on every start.
            ledger.MarkRequeued(completion.Id, null);
            _logger.LogDebug("Did not requeue an interrupted watch update, because its item is gone or ignored");
            return false;
        }
        var (kind, source, trigger) = completion.Trigger switch {
            CompletionTrigger.Toggle => (OperationKind.WatchedToggle, SyncUpdateSource.LegacyManual, SyncNotificationTrigger.LegacyManual),
            CompletionTrigger.ConfirmedManual => (OperationKind.ConfirmedManual, SyncUpdateSource.ConfirmedManual, SyncNotificationTrigger.ConfirmedManual),
            _ => (OperationKind.Playback, SyncUpdateSource.Playback, SyncNotificationTrigger.Playback),
        };
        Guid operationId = Guid.NewGuid();
        // Before enqueuing, so a second restart can never run it a third time.
        ledger.MarkRequeued(completion.Id, operationId);
        Guid userId = completion.UserId;
        var handle = runner.Enqueue(userId, kind, OperationSources.Describe(item), operation => Run(item, userId, source, trigger, operation), operationId);
        if (handle == null) {
            _logger.LogWarning("Interrupted update for {ItemName} was not requeued because AniDoki storage is unavailable", item.Name);
            return false;
        }
        _logger.LogDebug("Requeued the interrupted {OperationKind} update for {ItemName}", kind, item.Name);
        return true;
    }

    // As the trigger ran it, without a toast: the player session it belonged to ended with the restart.
    private async Task Run(BaseItem item, Guid userId, SyncUpdateSource source, SyncNotificationTrigger trigger, OperationContext operation) {
        var collector = new PlaybackOutcomeCollector(operation.Record);
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, arm, animeList, memoryCache, _delayer) {
            WatchState = watchState,
            ClientOverride = ClientOverride
        };
        try {
            await pipeline.Run(item, userId, true, collector, source, operation.CancellationToken);
        } finally {
            if (collector.HasConfirmedChange)
                manualMarks?.Supersede(userId, item.Id);
            discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, trigger, collector));
        }
    }
}
