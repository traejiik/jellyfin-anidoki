using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
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
    TriggerReplay replay,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory) : IHostedService {
    private readonly ILogger<CompletionRequeue> _logger = loggerFactory.CreateLogger<CompletionRequeue>();

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
        OperationKind kind = completion.Trigger switch {
            CompletionTrigger.Toggle => OperationKind.WatchedToggle,
            CompletionTrigger.ConfirmedManual => OperationKind.ConfirmedManual,
            _ => OperationKind.Playback,
        };
        Guid operationId = Guid.NewGuid();
        // Before enqueuing, so a second restart can never run it a third time.
        ledger.MarkRequeued(completion.Id, operationId);
        Guid userId = completion.UserId;
        // As the trigger ran it, without a toast: the player session it belonged to ended with the restart.
        var handle = runner.Enqueue(userId, kind, OperationSources.Describe(item), operation => replay.Run(item, userId, kind, null, operation), operationId,
            retry: new RetrySubject(item.Id, kind));
        if (handle == null) {
            _logger.LogWarning("Interrupted update for {ItemName} was not requeued because AniDoki storage is unavailable", item.Name);
            return false;
        }
        _logger.LogDebug("Requeued the interrupted {OperationKind} update for {ItemName}", kind, item.Name);
        return true;
    }
}
