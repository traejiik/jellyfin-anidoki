using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

[TestFixture]
public class OperationRunnerTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        (_store, _runner) = Create(_directory);
    }

    [TearDown]
    public void TearDown() {
        _runner.Dispose();
        Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ARunIsRecordedWithTheResultOfItsOutcomes() {
        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "Fixture Show · episode 5", operation => {
            operation.Record(Outcome(OutcomeKind.Confirmed, true));
            return Task.CompletedTask;
        })!;

        await handle.Completion;

        var view = _store.Read(handle.OperationId)!;
        Assert.That(view.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(view.Counts["Confirmed"], Is.EqualTo(1));
    }

    [Test]
    public async Task AnExplicitResultAndMessageWin() {
        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.SyncFromTrackers, "AniList", operation => {
            operation.Result = OperationState.Succeeded;
            operation.Message = "Checked 3 tracker entries.";
            operation.ReportProgress(3, 3);
            return Task.CompletedTask;
        })!;

        await handle.Completion;

        var view = _store.Read(handle.OperationId)!;
        Assert.That(view.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(view.Message, Is.EqualTo("Checked 3 tracker entries."));
        Assert.That(view.Done, Is.EqualTo(3));
    }

    [Test]
    public async Task OneUsersOperationsRunOneAtATimeInOrder() {
        Guid user = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var order = new List<string>();
        var first = _runner.Enqueue(user, OperationKind.Playback, "first", async _ => {
            order.Add("first");
            await release.Task;
        })!;
        var second = _runner.Enqueue(user, OperationKind.Playback, "second", _ => {
            order.Add("second");
            return Task.CompletedTask;
        })!;

        await WaitUntil(() => _store.Read(first.OperationId)!.State == OperationState.Running);
        Assert.That(_store.Read(second.OperationId)!.State, Is.EqualTo(OperationState.Queued));
        release.SetResult();
        await Task.WhenAll(first.Completion, second.Completion);

        Assert.That(order, Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public async Task DifferentUsersRunAtTheSameTime() {
        var release = new TaskCompletionSource();
        var blocked = _runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "blocked", _ => release.Task)!;
        await WaitUntil(() => _store.Read(blocked.OperationId)!.State == OperationState.Running);
        var other = _runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "other", _ => Task.CompletedTask)!;

        // The other user's operation finishes while the first user's is still running.
        await other.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(_store.Read(blocked.OperationId)!.State, Is.EqualTo(OperationState.Running));

        release.SetResult();
        await blocked.Completion;
    }

    [Test]
    public async Task CancellingAQueuedOperationNeverRunsIt() {
        Guid user = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var first = _runner.Enqueue(user, OperationKind.SyncToTrackers, "first", _ => release.Task)!;
        bool ran = false;
        var second = _runner.Enqueue(user, OperationKind.SyncToTrackers, "second", _ => {
            ran = true;
            return Task.CompletedTask;
        })!;

        Assert.That(_runner.Cancel(second.OperationId), Is.True);
        await second.Completion;
        release.SetResult();
        await first.Completion;

        Assert.That(ran, Is.False);
        Assert.That(_store.Read(second.OperationId)!.State, Is.EqualTo(OperationState.Cancelled));
        Assert.That(_store.Read(first.OperationId)!.State, Is.EqualTo(OperationState.NoChange));
    }

    [Test]
    public async Task CancellingARunningOperationStopsItAtItsNextCheck() {
        var started = new TaskCompletionSource();
        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.SyncToTrackers, "x", async operation => {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, operation.CancellationToken);
        })!;
        await started.Task;

        _runner.Cancel(handle.OperationId);
        await handle.Completion;

        var view = _store.Read(handle.OperationId)!;
        Assert.That(view.State, Is.EqualTo(OperationState.Cancelled));
        Assert.That(view.Message, Does.Contain("already sent were kept"));
    }

    [Test]
    public async Task AFailureIsRecordedAndTheNextOperationStillRuns() {
        Guid user = Guid.NewGuid();
        var failing = _runner.Enqueue(user, OperationKind.WatchedToggle, "x", _ => throw new InvalidOperationException("provider exploded"))!;
        var next = _runner.Enqueue(user, OperationKind.WatchedToggle, "y", operation => {
            operation.Record(Outcome(OutcomeKind.Confirmed, true));
            return Task.CompletedTask;
        })!;

        await Task.WhenAll(failing.Completion, next.Completion);

        Assert.That(_store.Read(failing.OperationId)!.State, Is.EqualTo(OperationState.Failed));
        Assert.That(_store.Read(next.OperationId)!.State, Is.EqualTo(OperationState.Succeeded));
    }

    [Test]
    public async Task StoppingInterruptsRunningOperations() {
        var started = new TaskCompletionSource();
        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.SyncFromTrackers, "x", async operation => {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, operation.CancellationToken);
        })!;
        await started.Task;

        await _runner.StopAsync(CancellationToken.None);

        Assert.That(handle.Completion.IsCompleted, Is.True);
        Assert.That(_store.Read(handle.OperationId)!.State, Is.EqualTo(OperationState.Interrupted));
    }

    [Test]
    public async Task StartingMarksLeftoversFromThePreviousRunInterrupted() {
        Guid leftover = Guid.NewGuid();
        _store.Create(leftover, Guid.NewGuid(), OperationKind.Playback, "x");

        await _runner.StartAsync(CancellationToken.None);
        await _runner.StopAsync(CancellationToken.None);

        Assert.That(_store.Read(leftover)!.State, Is.EqualTo(OperationState.Interrupted));
    }

    [Test]
    public void NothingRunsWhenStorageIsUnavailable() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        using var runner = new OperationRunner(new OperationStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), TimeProvider.System),
            NullLogger<OperationRunner>.Instance);
        bool ran = false;

        var handle = runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "x", _ => {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.That(handle, Is.Null);
        Assert.That(ran, Is.False);
    }

    [Test]
    public void CancellingAnUnknownOperationReportsFalse() => Assert.That(_runner.Cancel(Guid.NewGuid()), Is.False);

    [Test]
    public async Task AGivenOperationIdIsUsed() {
        Guid id = Guid.NewGuid();

        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.ConfirmedManual, "x", _ => Task.CompletedTask, id)!;
        await handle.Completion;

        Assert.That(handle.OperationId, Is.EqualTo(id));
        Assert.That(_store.Read(id), Is.Not.Null);
    }
}
