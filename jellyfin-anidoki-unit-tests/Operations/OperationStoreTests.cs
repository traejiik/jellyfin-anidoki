using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

[TestFixture]
public class OperationStoreTests {
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = NewDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void ACreatedOperationReadsAsQueued() {
        var (store, _) = Create(_directory);
        Guid id = Guid.NewGuid();
        Guid user = Guid.NewGuid();

        store.Create(id, user, OperationKind.SyncToTrackers, "All watched series");

        var view = store.Read(id)!;
        Assert.That(view.UserId, Is.EqualTo(user));
        Assert.That(view.Kind, Is.EqualTo(OperationKind.SyncToTrackers));
        Assert.That(view.State, Is.EqualTo(OperationState.Queued));
        Assert.That(view.Source, Is.EqualTo("All watched series"));
        Assert.That(view.StartedUtc, Is.Null);
        Assert.That(view.Counts, Is.Empty);
    }

    [Test]
    public void ProgressActionsAndTheResultAreRead() {
        var (store, _) = Create(_directory);
        Guid id = Guid.NewGuid();
        store.Create(id, Guid.NewGuid(), OperationKind.SyncToTrackers, "All watched series");

        store.MarkRunning(id);
        store.SetProgress(id, 2, 5);
        store.AddAction(id, 1, Outcome(OutcomeKind.Confirmed, true));
        store.AddAction(id, 2, Outcome(OutcomeKind.NoChange));
        store.AddAction(id, 3, Outcome(OutcomeKind.Confirmed, true));
        store.Finish(id, OperationState.Partial, "Checked 5 seasons.");

        var view = store.Read(id)!;
        Assert.That(view.State, Is.EqualTo(OperationState.Partial));
        Assert.That(view.Done, Is.EqualTo(2));
        Assert.That(view.Total, Is.EqualTo(5));
        Assert.That(view.Message, Is.EqualTo("Checked 5 seasons."));
        Assert.That(view.StartedUtc, Is.Not.Null);
        Assert.That(view.FinishedUtc, Is.Not.Null);
        Assert.That(view.Counts, Is.EquivalentTo(new Dictionary<string, int> { ["Confirmed"] = 2, ["NoChange"] = 1 }));
    }

    [Test]
    public void FindActiveSeesQueuedAndRunningOperationsOfThatKindOnly() {
        var (store, _) = Create(_directory);
        Guid user = Guid.NewGuid();
        Guid finished = Guid.NewGuid();
        Guid queued = Guid.NewGuid();
        store.Create(finished, user, OperationKind.SyncToTrackers, "x");
        store.Finish(finished, OperationState.Succeeded, null);
        store.Create(queued, user, OperationKind.SyncToTrackers, "x");
        store.Create(Guid.NewGuid(), user, OperationKind.SyncFromTrackers, "x");

        Assert.That(store.FindActive(user, OperationKind.SyncToTrackers), Is.EqualTo(queued));
        Assert.That(store.FindActive(Guid.NewGuid(), OperationKind.SyncToTrackers), Is.Null);
    }

    [Test]
    public void LeftoversFromAnEarlierRunAreMarkedInterrupted() {
        var (store, _) = Create(_directory);
        Guid queued = Guid.NewGuid();
        Guid running = Guid.NewGuid();
        Guid finished = Guid.NewGuid();
        store.Create(queued, Guid.NewGuid(), OperationKind.Playback, "x");
        store.Create(running, Guid.NewGuid(), OperationKind.Playback, "x");
        store.MarkRunning(running);
        store.Create(finished, Guid.NewGuid(), OperationKind.Playback, "x");
        store.Finish(finished, OperationState.Succeeded, null);

        Assert.That(store.MarkInterrupted(), Is.EqualTo(2));
        Assert.That(store.Read(queued)!.State, Is.EqualTo(OperationState.Interrupted));
        Assert.That(store.Read(running)!.State, Is.EqualTo(OperationState.Interrupted));
        Assert.That(store.Read(finished)!.State, Is.EqualTo(OperationState.Succeeded));
    }

    [Test]
    public void RetentionRemovesOnlyOldFinishedOperations() {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var (store, _) = Create(_directory, clock);
        Guid old = Guid.NewGuid();
        Guid stillQueued = Guid.NewGuid();
        store.Create(old, Guid.NewGuid(), OperationKind.Playback, "x");
        store.AddAction(old, 1, Outcome(OutcomeKind.Confirmed, true));
        store.Finish(old, OperationState.Succeeded, null);
        store.Create(stillQueued, Guid.NewGuid(), OperationKind.Playback, "x");
        clock.Now = clock.Now.AddDays(31);
        Guid recent = Guid.NewGuid();
        store.Create(recent, Guid.NewGuid(), OperationKind.Playback, "x");
        store.Finish(recent, OperationState.Succeeded, null);

        Assert.That(store.DeleteFinishedBefore(TimeSpan.FromDays(30)), Is.EqualTo(1));
        Assert.That(store.Read(old), Is.Null);
        Assert.That(store.Read(stillQueued), Is.Not.Null);
        Assert.That(store.Read(recent), Is.Not.Null);
    }

    [Test]
    public void OperationsForAUserReadNewestFirst() {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var (store, _) = Create(_directory, clock);
        Guid user = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        store.Create(first, user, OperationKind.Playback, "x");
        clock.Now = clock.Now.AddMinutes(1);
        store.Create(second, user, OperationKind.Playback, "x");
        store.Create(Guid.NewGuid(), Guid.NewGuid(), OperationKind.Playback, "x");

        Assert.That(store.ReadForUser(user).Select(view => view.OperationId), Is.EqualTo(new[] { second, first }));
    }

    [Test]
    public void AnUnavailableDatabaseThrows() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var store = new OperationStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => store.Create(Guid.NewGuid(), Guid.NewGuid(), OperationKind.Playback, "x"));
    }
}
