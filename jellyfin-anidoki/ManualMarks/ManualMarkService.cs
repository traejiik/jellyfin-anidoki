using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.ManualMarks;

public sealed class ManualMarkService(ManualMarkStore store, IUserManager users, ILibraryManager library,
    IUserDataManager data, ILoggerFactory loggers, IHttpContextAccessor context, IServerApplicationHost host,
    IHttpClientFactory clients, IApplicationPaths paths, IMemoryCache cache, Notifications.Discord.DiscordNotificationDispatcher discord) {
    /// <summary>Sends one confirmed manual mark. The operation runner already holds the user's sync gate.</summary>
    public async Task Execute(Guid operationId, OperationContext operation) {
        if (!store.TryBegin(operationId, out var request) || request == null) {
            operation.Result = OperationState.Cancelled;
            operation.Message = "The mark was no longer waiting to be sent.";
            return;
        }
        var collector = new PlaybackOutcomeCollector(operation.Record);
        Episode? sourceItem = null;
        try {
            var user = users.GetUserById(request.UserId);
            var item = sourceItem = library.GetItemById(request.SourceItemId) as Episode;
            var configuration = Plugin.Instance?.PluginConfiguration;
            if (user == null || item == null || !item.IsVisible(user, false) || !data.GetUserData(user, item).Played ||
                configuration?.watchedTickboxUpdatesProvider != true || SyncHelper.MediaShouldBeIgnored(item) ||
                !ManualMarkRequestFilter.IsTracked(request.UserId, item, library, loggers.CreateLogger<ManualMarkService>())) {
                Cancel(operationId, operation, "The item, access or tracking settings changed. Trackers were not updated.");
                return;
            }
            var fingerprint = ManualMarkAccounts.Fingerprint(request.UserId);
            if (string.IsNullOrEmpty(fingerprint) || fingerprint != request.AccountFingerprint) {
                Cancel(operationId, operation, "Linked accounts changed. Mark again to approve the current accounts.");
                return;
            }
            operation.CancellationToken.ThrowIfCancellationRequested();
            var updater = new UpdateProviderStatus(library, loggers, context, host, clients, paths, cache, new Delayer());
            await updater.UpdateInsideGate(item, request.UserId, true, collector, SyncUpdateSource.ConfirmedManual, operation.CancellationToken);
            discord.Enqueue(SyncOperationSummary.Capture(item, request.UserId, operationId, SyncNotificationTrigger.ConfirmedManual, collector));
            var outcomes = collector.Outcomes;
            var hasChange = collector.HasConfirmedChange;
            var state = hasChange ? outcomes.Any(o => o.Kind is OutcomeKind.Failed or OutcomeKind.Unconfirmed or OutcomeKind.Unresolved) ? ManualMarkState.Partial : ManualMarkState.Confirmed :
                outcomes.Any(o => o.Kind == OutcomeKind.Unconfirmed) ? ManualMarkState.Unconfirmed :
                outcomes.Any(o => o.Kind == OutcomeKind.Failed) ? ManualMarkState.Failed :
                outcomes.Any(o => o.Kind == OutcomeKind.NoChange) ? ManualMarkState.NoChange :
                outcomes.Any(o => o.Kind == OutcomeKind.Skipped) ? ManualMarkState.Skipped : ManualMarkState.Unresolved;
            var presentation = PublicNotificationEvent.From(new(Guid.NewGuid(), operationId, 0, request.CreatedUtc,
                request.UserId, "", request.SourceItemId, request.SourceTitle, request.SourceEpisode, false, outcomes));
            store.Complete(operationId, state, presentation.Outcomes, state switch {
                ManualMarkState.Confirmed => "Trackers updated.",
                ManualMarkState.Partial => "Some trackers updated. Other results need attention.",
                ManualMarkState.NoChange => "No tracker changes were needed.",
                ManualMarkState.Unconfirmed => "The update may have taken effect. Check the tracker before retrying.",
                ManualMarkState.Failed => "Not retried automatically.",
                ManualMarkState.Skipped => "Skipped by your tracking settings.",
                _ => "Check linking and tracking settings."
            });
        } catch (Exception) {
            if (collector.HasConfirmedChange && sourceItem != null)
                discord.Enqueue(SyncOperationSummary.Capture(sourceItem, request.UserId, operationId, SyncNotificationTrigger.ConfirmedManual, collector));
            var outcomes = PublicNotificationEvent.From(new(Guid.NewGuid(), operationId, 0, request.CreatedUtc,
                request.UserId, "", request.SourceItemId, request.SourceTitle, request.SourceEpisode, false, collector.Outcomes)).Outcomes;
            store.Complete(operationId, collector.HasConfirmedChange ? ManualMarkState.Partial : ManualMarkState.Unconfirmed,
                outcomes, "Updating stopped. A transmitted tracker update may have taken effect; it will not be retried automatically.");
            loggers.CreateLogger<ManualMarkService>().LogWarning("Manual tracker operation ended without a complete confirmed result");
        }
    }

    private void Cancel(Guid operationId, OperationContext operation, string reason) {
        store.Complete(operationId, ManualMarkState.Cancelled, [], reason);
        operation.Result = OperationState.Cancelled;
        operation.Message = reason;
    }
}
