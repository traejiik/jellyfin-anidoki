using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Simkl;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.LoggingTests;

/// <summary>Every line written while an operation runs carries the operation's IDs, whichever class wrote it.</summary>
[TestFixture, NonParallelizable]
public class OperationScopeTests {
    private string _directory = null!;
    private CapturedLogs _logs = null!;
    private OperationRunner _runner = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _logs = new CapturedLogs();
        var store = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        _runner = new OperationRunner(store, _logs.For<OperationRunner>());
    }

    [TearDown]
    public void TearDown() {
        _runner.Dispose();
        Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ALineFromAnotherClassCarriesTheOperation() {
        Guid userId = Guid.NewGuid();
        ILogger stage = _logs.CreateLogger("jellyfin_anidoki.Sync");
        var handle = _runner.Enqueue(userId, OperationKind.SyncToTrackers, "All linked trackers", _ => {
            stage.LogInformation("({Provider}) {Title}: Confirmed", ApiName.AniList, "Fixture Show");
            return Task.CompletedTask;
        })!;
        await handle.Completion;

        LogEntry line = _logs.Entries.Single(entry => entry.Category == "jellyfin_anidoki.Sync");
        Assert.That(line.Scope["OperationId"], Is.EqualTo(handle.OperationId));
        Assert.That(line.Scope["OperationKind"], Is.EqualTo(OperationKind.SyncToTrackers));
        Assert.That(line.Scope["UserId"], Is.EqualTo(userId));
    }

    [Test]
    public async Task TheOperationsOwnLinesKeepTheirIdsAndAreInsideTheScope() {
        var handle = _runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "Fixture Show · episode 5", _ => Task.CompletedTask)!;
        await handle.Completion;

        LogEntry started = _logs.Entries.Single(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("started"));
        LogEntry finished = _logs.Entries.Single(entry => entry.Message.Contains("finished as"));
        Assert.That(started.Properties["OperationId"], Is.EqualTo(handle.OperationId));
        Assert.That(finished.Properties["OperationId"], Is.EqualTo(handle.OperationId));
        Assert.That(finished.Scope["OperationId"], Is.EqualTo(handle.OperationId));
    }

    [Test]
    public async Task TwoUsersAtOnceEachSeeOnlyTheirOwnOperationAndNothingLeaks() {
        ILogger stage = _logs.CreateLogger("stage");
        var bothRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        Func<OperationContext, Task> run = async operation => {
            if (Interlocked.Increment(ref running) == 2)
                bothRunning.SetResult();
            await bothRunning.Task;
            stage.LogInformation("Step for user {UserId}", operation.UserId);
        };
        var first = _runner.Enqueue(Guid.NewGuid(), OperationKind.SyncToTrackers, "a", run)!;
        var second = _runner.Enqueue(Guid.NewGuid(), OperationKind.SyncToTrackers, "b", run)!;
        await Task.WhenAll(first.Completion, second.Completion);
        stage.LogInformation("After both operations");

        LogEntry[] steps = _logs.Entries.Where(entry => entry.Message.StartsWith("Step")).ToArray();
        Assert.That(steps, Has.Length.EqualTo(2));
        foreach (LogEntry step in steps)
            Assert.That(step.Scope["UserId"], Is.EqualTo(step.Properties["UserId"]));
        Assert.That(_logs.Entries.Single(entry => entry.Message == "After both operations").Scope, Is.Empty);
    }
}
