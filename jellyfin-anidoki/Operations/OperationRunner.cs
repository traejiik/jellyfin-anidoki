using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Operations;

/// <summary>
/// Records every sync as an operation and runs it in the background, one at a time per user through
/// <see cref="SyncExecutionGate"/>. Work run here already holds the user's gate; it must call
/// <c>UpdateProviderStatus.UpdateInsideGate</c>, never <c>Update</c>, or it waits on itself forever.
/// </summary>
public sealed class OperationRunner(OperationStore store, ILogger<OperationRunner> logger) : IHostedService, IDisposable {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, Task Completion)> _active = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _retention;

    public sealed record Handle(Guid OperationId, Task Completion);

    /// <summary>Records the operation as queued and starts it after any earlier operation for the same user.</summary>
    /// <returns>Null when it could not be recorded because storage is unavailable; nothing runs then.</returns>
    public Handle? Enqueue(Guid userId, OperationKind kind, string source, Func<OperationContext, Task> run, Guid? operationId = null) {
        Guid id = operationId ?? Guid.NewGuid();
        try {
            store.Create(id, userId, kind, source);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("{OperationKind} for user {UserId} was not started because AniDoki storage is unavailable: {ErrorMessage}", kind, userId, error.Message);
            return null;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        // Joining the gate's queue here, before returning, keeps one user's operations in the order they were enqueued.
        Task<IDisposable> gate = SyncExecutionGate.Shared.Enter(userId, cancellation.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active[id] = (cancellation, completion.Task);
        _ = Execute(id, userId, kind, run, gate, cancellation, completion);
        return new Handle(id, completion.Task);
    }

    /// <summary>Cancels a queued or running operation. Writes already sent are kept.</summary>
    public bool Cancel(Guid operationId) {
        if (!_active.TryGetValue(operationId, out var active))
            return false;
        try {
            active.Cancellation.Cancel();
            return true;
        } catch (ObjectDisposedException) {
            return false;
        }
    }

    /// <summary>Completes when every operation started so far has finished.</summary>
    internal Task Drain() => Task.WhenAll(_active.Values.Select(active => active.Completion).ToArray());

    public Task StartAsync(CancellationToken cancellationToken) {
        try {
            int interrupted = store.MarkInterrupted();
            if (interrupted > 0)
                logger.LogInformation("Marked {OperationCount} operations left by the previous run as interrupted", interrupted);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not review operations left by the previous run: {ErrorMessage}", error.Message);
        }
        _retention = new Timer(_ => RemoveOldOperations(), null, TimeSpan.Zero, TimeSpan.FromDays(1));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _retention?.Dispose();
        _stopping.Cancel();
        try {
            await Drain().WaitAsync(cancellationToken);
        } catch (OperationCanceledException) {
            logger.LogWarning("Stopped before every operation had finished");
        }
    }

    public void Dispose() {
        _retention?.Dispose();
        _stopping.Dispose();
    }

    private async Task Execute(Guid id, Guid userId, OperationKind kind, Func<OperationContext, Task> run, Task<IDisposable> gate,
        CancellationTokenSource cancellation, TaskCompletionSource completion) {
        var context = new OperationContext(id, userId, store, logger, cancellation.Token);
        IDisposable? lease = null;
        try {
            // Return to the caller (often an event handler) before any work starts.
            await Task.Yield();
            lease = await gate;
            cancellation.Token.ThrowIfCancellationRequested();
            store.MarkRunning(id);
            await run(context);
            OperationState state = context.Result ?? OperationStates.FromOutcomes(context.Outcomes);
            store.Finish(id, state, context.Message);
            logger.LogInformation("{OperationKind} {OperationId} finished as {OperationState}", kind, id, state);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            bool stopping = _stopping.IsCancellationRequested;
            OperationState state = stopping ? OperationState.Interrupted : OperationState.Cancelled;
            TryFinish(id, state, context.Message ?? (stopping
                ? "Jellyfin stopped before this finished. Updates already sent were kept."
                : "Cancelled. Updates already sent were kept."));
            logger.LogInformation("{OperationKind} {OperationId} finished as {OperationState}", kind, id, state);
        } catch (Exception error) {
            logger.LogError(error, "{OperationKind} {OperationId} stopped unexpectedly", kind, id);
            TryFinish(id, OperationState.Failed, "Stopped unexpectedly. Updates already sent were kept; see the Jellyfin log.");
        } finally {
            lease?.Dispose();
            _active.TryRemove(id, out _);
            cancellation.Dispose();
            completion.TrySetResult();
        }
    }

    private void TryFinish(Guid id, OperationState state, string message) {
        try {
            store.Finish(id, state, message);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not record the result of operation {OperationId}: {ErrorMessage}", id, error.Message);
        }
    }

    private void RemoveOldOperations() {
        try {
            int removed = store.DeleteFinishedBefore(Retention);
            if (removed > 0)
                logger.LogDebug("Removed {OperationCount} operations older than {RetentionDays} days", removed, Retention.TotalDays);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not remove old operations: {ErrorMessage}", error.Message);
        }
    }
}
