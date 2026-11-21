using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Playback;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.ManualMarks;

/// <summary>Hands confirmed manual marks to the operation runner, which runs them one at a time per user.</summary>
public sealed class ManualMarkWorker(ManualMarkQueue queue, ManualMarkStore store, ManualMarkService service, OperationRunner runner,
    CompletionLedger ledger, ILogger<ManualMarkWorker> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken cancellation) {
        await foreach (var operationId in queue.Channel.Reader.ReadAllAsync(cancellation)) {
            var request = store.QueuedRequest(operationId);
            if (request == null)
                continue;
            if (!Admit(request)) {
                // The in-memory prompt says no further confirmation is needed, as it does after a playback update.
                store.Supersede(request.UserId, request.SourceItemId);
                continue;
            }
            string source = request.SourceEpisode is { } episode ? $"{request.SourceTitle} · episode {episode}" : request.SourceTitle;
            if (runner.Enqueue(request.UserId, OperationKind.ConfirmedManual, source, operation => service.Execute(operationId, operation), operationId) == null) {
                store.Complete(operationId, ManualMarkState.Failed, [], "AniDoki storage is unavailable, so trackers were not updated.");
                logger.LogWarning("Manual mark {OperationId} was not sent because AniDoki storage is unavailable", operationId);
            }
        }
    }

    /// <summary>
    /// Asks the ledger; false drops a mark confirmed within <see cref="CompletionLedger.DedupeWindow"/> of a completion
    /// of the same item. Storage errors let the mark through, and enqueuing then reports them.
    /// </summary>
    private bool Admit(ManualMarkRequest request) {
        try {
            if (ledger.TryRecord(request.UserId, request.SourceItemId, request.OperationId.ToString("N"), CompletionTrigger.ConfirmedManual,
                    request.OperationId, CompletionLedger.DedupeWindow))
                return true;
            logger.LogDebug("Dropped the confirmed mark for {Title}, because a recent watch already updated trackers", request.SourceTitle);
            return false;
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogDebug("Could not check the completion ledger for {Title}: {ErrorMessage}", request.SourceTitle, error.Message);
            return true;
        }
    }
}
