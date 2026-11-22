using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

/// <summary>The runner hands each finished update's outcomes to the retry queue, and only updates that may be retried.</summary>
[TestFixture]
public class RetryHookTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private RetryQueue _retries = null!;
    private OperationRunner _runner = null!;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _store = new OperationStore(database, TimeProvider.System);
        _retries = new RetryQueue(database, TimeProvider.System, _ => "season");
        _runner = new OperationRunner(_store, NullLogger<OperationRunner>.Instance, _retries);
    }

    [TearDown]
    public void TearDown() {
        _runner.Dispose();
        Directory.Delete(_directory, true);
    }

    private static Func<OperationContext, Task> Fails(string step) => operation => {
        operation.Record(new ProviderWriteOutcome(ApiName.AniList, "500", "Fixture Show", OutcomeKind.Failed, null, null, false, step, false));
        return Task.CompletedTask;
    };

    [Test]
    public async Task AFailedPlaybackUpdateLeavesARetry() {
        var handle = _runner.Enqueue(_user, OperationKind.Playback, "Fixture Show · episode 5", Fails("transient"),
            retry: new RetrySubject(_item, OperationKind.Playback))!;

        await handle.Completion;

        var row = _retries.ForUser(_user).Single();
        Assert.That(row.LastOperationId, Is.EqualTo(handle.OperationId));
        Assert.That(row.Trigger, Is.EqualTo(OperationKind.Playback));
    }

    [Test]
    public async Task AnUpdateWithoutASubjectIsNeverRetried() {
        // Manual syncs, previews and applies pass no subject: an admin is watching them.
        var handle = _runner.Enqueue(_user, OperationKind.SyncToTrackers, "AniList", Fails("transient"))!;

        await handle.Completion;

        Assert.That(_retries.ForUser(_user), Is.Empty);
    }

    [Test]
    public async Task ACancelledUpdateIsNotRetried() {
        var started = new TaskCompletionSource();
        var handle = _runner.Enqueue(_user, OperationKind.Playback, "Fixture Show · episode 5", async operation => {
            operation.Record(new ProviderWriteOutcome(ApiName.AniList, "500", "Fixture Show", OutcomeKind.Failed, null, null, false, "transient", false));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, operation.CancellationToken);
        }, retry: new RetrySubject(_item, OperationKind.Playback))!;
        await started.Task;

        _runner.Cancel(handle.OperationId);
        await handle.Completion;

        Assert.That(_retries.ForUser(_user), Is.Empty);
    }

    [Test]
    public async Task ARetryOperationRecordsItsAttempt() {
        var handle = _runner.Enqueue(_user, OperationKind.Retry, "Fixture Show · episode 5", Fails("transient"),
            retry: new RetrySubject(_item, OperationKind.Playback, 2, ApiName.AniList))!;

        await handle.Completion;

        var view = _store.Read(handle.OperationId)!;
        Assert.That(view.Kind, Is.EqualTo(OperationKind.Retry));
        Assert.That(view.RetryAttempt, Is.EqualTo(2));
        Assert.That(_store.Read(_runner.Enqueue(_user, OperationKind.Playback, "x", _ => Task.CompletedTask)!.OperationId)!.RetryAttempt, Is.Null);
    }

    [Test]
    public async Task ARetryRecordsTheUpdateItFollowedSoHistoryShowsThemAsOne() {
        var original = _runner.Enqueue(_user, OperationKind.Playback, "Fixture Show · episode 5", Fails("transient"),
            retry: new RetrySubject(_item, OperationKind.Playback))!;
        await original.Completion;
        var retry = _runner.Enqueue(_user, OperationKind.Retry, "Fixture Show · episode 5", operation => {
            operation.Record(new ProviderWriteOutcome(ApiName.AniList, "500", "Fixture Show", OutcomeKind.Confirmed, 5, null, false, "progress", true));
            return Task.CompletedTask;
        }, retry: new RetrySubject(_item, OperationKind.Playback, 1, ApiName.AniList, original.OperationId))!;
        await retry.Completion;

        var entry = new HistoryReader(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System).Read(_user, 0, 10).Entries.Single();

        Assert.That(entry.OperationId, Is.EqualTo(original.OperationId));
        Assert.That(entry.Results.Single(), Is.EqualTo(new HistoryResult("AniList", "Updated", 2)));
    }

    [Test]
    public async Task AnUnavailableRetryQueueDoesNotFailTheUpdate() {
        File.WriteAllText(Path.Combine(_directory, "file-not-a-folder"), "x");
        var broken = new RetryQueue(new AniDokiDatabase(Path.Combine(_directory, "file-not-a-folder", "x.db")), TimeProvider.System, _ => "season");
        using var runner = new OperationRunner(_store, NullLogger<OperationRunner>.Instance, broken);

        var handle = runner.Enqueue(_user, OperationKind.Playback, "Fixture Show · episode 5", Fails("transient"),
            retry: new RetrySubject(_item, OperationKind.Playback))!;
        await handle.Completion;

        Assert.That(_store.Read(handle.OperationId)!.State, Is.EqualTo(OperationState.Failed));
    }
}
