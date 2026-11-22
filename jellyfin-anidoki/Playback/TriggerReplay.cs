using System;
using System.Net.Http;
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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// Runs a playback, watched-mark or confirmed-mark update again, later and away from its player: for a restart that
/// cut it off (<see cref="CompletionRequeue"/>) or a tracker that failed it (<see cref="RetryWorker"/>). There is no
/// toast, since the player session it belonged to has ended; Discord still hears of a confirmed change.
/// </summary>
public sealed class TriggerReplay(
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
    ISeasonWatchState? watchState = null) {
    private readonly IAsyncDelayer _delayer = new Delayer();

    /// <summary>Test seam: the tracker clients the pipelines use.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; init; }

    /// <param name="trigger">Playback, WatchedToggle or ConfirmedManual: which update rules apply.</param>
    /// <param name="only">The one tracker a retry updates; null for all the user's trackers.</param>
    public async Task Run(BaseItem item, Guid userId, OperationKind trigger, ApiName? only, OperationContext operation) {
        var (source, notification) = trigger switch {
            OperationKind.WatchedToggle => (SyncUpdateSource.LegacyManual, SyncNotificationTrigger.LegacyManual),
            OperationKind.ConfirmedManual => (SyncUpdateSource.ConfirmedManual, SyncNotificationTrigger.ConfirmedManual),
            _ => (SyncUpdateSource.Playback, SyncNotificationTrigger.Playback),
        };
        var collector = new PlaybackOutcomeCollector(operation.Record);
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory, arm, animeList, memoryCache, _delayer) {
            WatchState = watchState,
            ClientOverride = ClientOverride,
            OnlyProvider = only
        };
        try {
            await pipeline.Run(item, userId, true, collector, source, operation.CancellationToken);
        } finally {
            if (collector.HasConfirmedChange)
                manualMarks?.Supersede(userId, item.Id);
            discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, notification, collector));
        }
    }
}
