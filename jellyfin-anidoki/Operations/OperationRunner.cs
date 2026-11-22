using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Operations;

/// <summary>
/// Records every sync as an operation and runs it in the background, one at a time per user through
/// <see cref="SyncExecutionGate"/>. Work run here already holds the user's gate, and <c>SyncPipeline.Run</c> never takes it.
/// </summary>
/// <remarks>When an update that may be retried finishes, its outcomes go to <see cref="RetryQueue"/> (D6).</remarks>
public sealed class OperationRunner(OperationStore store, ILogger<OperationRunner> logger, RetryQueue? retries = null) : IHostedService, IDisposable {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, Task Completion)> _active = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _retention;

    public sealed record Handle(Guid OperationId, Task Completion);

    /// <summary>Records the operation as queued and starts it after any earlier operation for the same user.</summary>
    /// <returns>Null when it could not be recorded because storage is unavailable; nothing runs then.</returns>
    /// <param name="previewOf">The preview an apply writes the selected rows of.</param>
    /// <param name="retry">What a playback, watched-mark or confirmed-mark update was for, so a failure that may pass is
    /// retried. Manual syncs, previews and applies pass none: an admin is watching them.</param>
    public Handle? Enqueue(Guid userId, OperationKind kind, string source, Func<OperationContext, Task> run, Guid? operationId = null,
        Guid? previewOf = null, RetrySubject? retry = null) {
        Guid id = operationId ?? Guid.NewGuid();
        try {
            store.Create(id, userId, kind, source, previewOf, kind == OperationKind.Retry ? retry?.Attempt : null);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("{OperationKind} for user {UserId} was not started because AniDoki storage is unavailable: {ErrorMessage}", kind, userId, error.Message);
            return null;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        // Joining the gate's queue here, before returning, keeps one user's operations in the order they were enqueued.
        Task<IDisposable> gate = SyncExecutionGate.Shared.Enter(userId, cancellation.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active[id] = (cancellation, completion.Task);
        _ = Execute(id, userId, kind, run, retry, gate, cancellation, completion);
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
                logger.LogInformation("Marked {Count} operations left by the previous run as interrupted", interrupted);
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
            logger.LogWarning("Jellyfin stopped before every operation had finished");
        }
    }

    public void Dispose() {
        _retention?.Dispose();
        _stopping.Dispose();
    }

    private async Task Execute(Guid id, Guid userId, OperationKind kind, Func<OperationContext, Task> run, RetrySubject? retry, Task<IDisposable> gate,
        CancellationTokenSource cancellation, TaskCompletionSource completion) {
        var context = new OperationContext(id, userId, store, logger, cancellation.Token);
        IDisposable? lease = null;
        IDisposable? scope = null;
        try {
            // Return to the caller (often an event handler) before any work starts. The scope opens after this, so it never reaches the caller.
            await Task.Yield();
            // Every line written while this runs, by any class, carries these. The stock log templates do not print scopes,
            // so the operation's own started and finished lines still name it.
            scope = logger.BeginScope(new Dictionary<string, object> {
                ["OperationId"] = id,
                ["OperationKind"] = kind,
                ["UserId"] = userId,
            });
            lease = await gate;
            cancellation.Token.ThrowIfCancellationRequested();
            logger.LogDebug("{OperationKind} {OperationId} started for user {UserId}", kind, id, userId);
            store.MarkRunning(id);
            await run(context);
            OperationState state = context.Result ?? OperationStates.FromOutcomes(context.Outcomes);
            store.Finish(id, state, context.Message);
            logger.LogInformation("{OperationKind} {OperationId} finished as {OperationState}", kind, id, state);
            if (retry != null)
                ScheduleRetries(id, userId, retry, context.Outcomes);
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
            scope?.Dispose();
        }
    }

    /// <summary>Hands the outcomes to the retry queue. A storage failure here never fails the update that already ran.</summary>
    private void ScheduleRetries(Guid id, Guid userId, RetrySubject retry, IReadOnlyCollection<ProviderWriteOutcome> outcomes) {
        if (retries == null)
            return;
        IReadOnlyList<RetryDecision> decisions;
        try {
            decisions = retries.AfterOperation(id, userId, retry, outcomes);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not schedule tracker retries: {ErrorMessage}", error.Message);
            return;
        }
        foreach (var decision in decisions) {
            switch (decision.Kind) {
                case RetryDecisionKind.Scheduled:
                    int minutes = (int)Math.Ceiling(((decision.DueUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalMinutes);
                    logger.LogInformation("({Provider}) Will retry {Title} in {Minutes} minutes, attempt {Attempt} of {MaxAttempts}",
                        decision.Provider, decision.Title, Math.Max(minutes, 1), decision.Attempt, RetryQueue.MaxAttempts);
                    break;
                case RetryDecisionKind.Dropped:
                    logger.LogInformation("({Provider}) Gave up updating {Title} after {Count} attempts", decision.Provider, decision.Title, decision.Attempt);
                    break;
                default:
                    logger.LogDebug("({Provider}) Cleared the pending retry for {Title}", decision.Provider, decision.Title);
                    break;
            }
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
                logger.LogDebug("Removed {Count} operations older than {RetentionDays} days", removed, Retention.TotalDays);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not remove old operations: {ErrorMessage}", error.Message);
        }
    }
}
