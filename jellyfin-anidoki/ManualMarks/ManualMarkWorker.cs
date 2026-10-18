using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.ManualMarks;

/// <summary>Hands confirmed manual marks to the operation runner, which runs them one at a time per user.</summary>
public sealed class ManualMarkWorker(ManualMarkQueue queue, ManualMarkStore store, ManualMarkService service, OperationRunner runner,
    ILogger<ManualMarkWorker> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken cancellation) {
        await foreach (var operationId in queue.Channel.Reader.ReadAllAsync(cancellation)) {
            var request = store.QueuedRequest(operationId);
            if (request == null)
                continue;
            string source = request.SourceEpisode is { } episode ? $"{request.SourceTitle} · episode {episode}" : request.SourceTitle;
            if (runner.Enqueue(request.UserId, OperationKind.ConfirmedManual, source, operation => service.Execute(operationId, operation), operationId) == null) {
                store.Complete(operationId, ManualMarkState.Failed, [], "AniDoki storage is unavailable, so trackers were not updated.");
                logger.LogWarning("Manual mark {OperationId} was not sent because AniDoki storage is unavailable", operationId);
            }
        }
    }
}
