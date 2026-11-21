using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki_unit_tests.Operations;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Playback;

[TestFixture]
public class CompletionLedgerTests {
    private string _directory = null!;
    private MutableTime _clock = null!;
    private OperationStore _store = null!;
    private CompletionLedger _ledger = null!;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _clock = new MutableTime(new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero));
        (_store, _) = Create(_directory, _clock);
        _ledger = Ledger(_directory, _clock);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void ASecondCompletionOfOneCycleIsDropped() {
        Assert.That(_ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid()), Is.True);
        Assert.That(_ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid()), Is.False);
        Assert.That(_ledger.RecentFor(_user, _item, CompletionLedger.DedupeWindow), Has.Count.EqualTo(1));
    }

    [Test]
    public void EachUserOnOneCycleGetsTheirOwnCompletion() {
        Assert.That(_ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid()), Is.True);
        Assert.That(_ledger.TryRecord(Guid.NewGuid(), _item, "session", CompletionTrigger.Stop, Guid.NewGuid()), Is.True);
    }

    [Test]
    public void ANewCycleOnTheSameItemIsARewatch() {
        Assert.That(_ledger.TryRecord(_user, _item, "first", CompletionTrigger.Stop, Guid.NewGuid()), Is.True);
        Assert.That(_ledger.TryRecord(_user, _item, "second", CompletionTrigger.Stop, Guid.NewGuid()), Is.True);
    }

    [Test]
    public void AToggleWithinSixHoursIsDroppedAndAfterIsRecorded() {
        _ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid());

        _clock.Now += TimeSpan.FromMinutes(10);
        Assert.That(_ledger.TryRecord(_user, _item, "toggle-1", CompletionTrigger.Toggle, Guid.NewGuid(), CompletionLedger.DedupeWindow), Is.False);

        _clock.Now += TimeSpan.FromHours(7);
        Assert.That(_ledger.TryRecord(_user, _item, "toggle-2", CompletionTrigger.Toggle, Guid.NewGuid(), CompletionLedger.DedupeWindow), Is.True);
    }

    [Test]
    public void AToggleOfAnotherItemIsNotDropped() {
        _ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid());

        Assert.That(_ledger.TryRecord(_user, Guid.NewGuid(), "toggle", CompletionTrigger.Toggle, Guid.NewGuid(), CompletionLedger.DedupeWindow), Is.True);
    }

    [Test]
    public void EndingTheWindowLetsTheNextToggleThrough() {
        _ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid());

        Assert.That(_ledger.EndWindow(_user, _item), Is.EqualTo(1));

        Assert.That(_ledger.TryRecord(_user, _item, "toggle", CompletionTrigger.Toggle, Guid.NewGuid(), CompletionLedger.DedupeWindow), Is.True);
    }

    [Test]
    public async Task TwoReportsAtOnceRecordOneCompletion() {
        var start = new TaskCompletionSource();
        Task<bool>[] attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () => {
            await start.Task;
            return _ledger.TryRecord(_user, _item, "session", CompletionTrigger.Stop, Guid.NewGuid());
        })).ToArray();

        start.SetResult();
        bool[] results = await Task.WhenAll(attempts);

        Assert.That(results.Count(recorded => recorded), Is.EqualTo(1));
    }

    [Test]
    public void OnlyCompletionsOfInterruptedOperationsNotYetRequeuedAreListed() {
        Guid interrupted = Guid.NewGuid();
        Guid finished = Guid.NewGuid();
        _store.Create(interrupted, _user, OperationKind.Playback, "Fixture Show · episode 5");
        _store.Create(finished, _user, OperationKind.Playback, "Fixture Show · episode 6");
        _store.Finish(finished, OperationState.Succeeded, null);
        _store.MarkInterrupted();
        _ledger.TryRecord(_user, _item, "interrupted", CompletionTrigger.Stop, interrupted);
        _ledger.TryRecord(_user, Guid.NewGuid(), "finished", CompletionTrigger.Stop, finished);

        var listed = _ledger.Interrupted().Single();
        Assert.That((listed.ItemId, listed.OperationId, listed.Trigger), Is.EqualTo((_item, (Guid?)interrupted, CompletionTrigger.Stop)));

        _ledger.MarkRequeued(listed.Id, Guid.NewGuid());
        Assert.That(_ledger.Interrupted(), Is.Empty);
    }

    [Test]
    public void CompletionsArePrunedWithOperationsAfterThirtyDays() {
        _ledger.TryRecord(_user, _item, "old", CompletionTrigger.Stop, Guid.NewGuid());
        _clock.Now += TimeSpan.FromDays(29);
        _ledger.TryRecord(_user, _item, "recent", CompletionTrigger.Stop, Guid.NewGuid());
        _clock.Now += TimeSpan.FromDays(2);

        _store.DeleteFinishedBefore(OperationRunner.Retention);

        Assert.That(_ledger.RecentFor(_user, _item, TimeSpan.FromDays(365)).Select(row => row.CycleKey), Is.EqualTo(new[] { "recent" }));
    }
}
