using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Trackers;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;
using static jellyfin_anidoki_unit_tests.Trackers.TrackerTestSupport;

namespace jellyfin_anidoki_unit_tests.LoggingTests;

/// <summary>The Information lines an admin reads at the default level: one per outcome, and what each says.</summary>
[TestFixture, NonParallelizable]
public class OutcomeLogTests {
    private string _directory = null!;
    private CapturedLogs _logs = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _logs = new CapturedLogs();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task AWriteLogsOneOutcomeLineLedByTheProvider() {
        var (adapter, fake) = Adapter(ApiName.AniList);
        fake.Add(SyncHarness.Entry(500, "Fixture Show", 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 }));
        var played = new Resolved(new ResolvedTarget(Ref(ApiName.AniList), 5, TargetRole.Played, "Fixture Show"), ResolvedBy.Ids, []);

        await new Executor(adapter, new PlanRules(false, false), PlanPolicy.Automatic, new PlaybackOutcomeCollector(), new FixedTime(Now),
            _logs.CreateLogger("test")).Execute(played, CancellationToken.None);

        LogEntry outcome = _logs.Entries.Single(entry => entry.Level == LogLevel.Information);
        Assert.That(outcome.Message, Is.EqualTo("(AniList) Fixture Show: Confirmed (progress)"));
        Assert.That(outcome.Properties["Provider"], Is.EqualTo(ApiName.AniList));
        Assert.That(_logs.Entries.Where(entry => entry.Level >= LogLevel.Warning), Is.Empty);
    }

    [Test]
    public async Task AnOperationLogsHowItFinished() {
        var store = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        using var runner = new OperationRunner(store, _logs.For<OperationRunner>());

        var handle = runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "Fixture Show · episode 5", operation => {
            operation.Record(Outcome(OutcomeKind.Confirmed, true));
            return Task.CompletedTask;
        })!;
        await handle.Completion;

        LogEntry finished = _logs.Entries.Single(entry => entry.Level == LogLevel.Information);
        Assert.That(finished.Message, Is.EqualTo($"Playback {handle.OperationId} finished as Succeeded"));
        Assert.That(finished.Category, Is.EqualTo(typeof(OperationRunner).FullName));
    }

    [Test]
    public async Task AnOperationThatThrowsLogsTheException() {
        var store = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        using var runner = new OperationRunner(store, _logs.For<OperationRunner>());
        var failure = new InvalidOperationException("boom");

        var handle = runner.Enqueue(Guid.NewGuid(), OperationKind.Playback, "Fixture Show · episode 5", _ => throw failure)!;
        await handle.Completion;

        LogEntry stopped = _logs.Entries.Single(entry => entry.Level == LogLevel.Error);
        Assert.That(stopped.Exception, Is.SameAs(failure));
        Assert.That(stopped.Message, Is.EqualTo($"Playback {handle.OperationId} stopped unexpectedly"));
    }

    [Test]
    public async Task ASyncFromTrackersLogsOneLinePerOutcomeAndNoErrors() {
        using var harness = new InboundHarness();
        harness.Map(7001, 9001, "1");
        harness.Ids("anilist", 1001, 7001);
        harness.Series("Alpha", 1001, (1, 4));
        // A library series with no AniList or AniDB ID, and a tracker entry ARM does not know: both are expected skips.
        harness.Series("No IDs", 1002, (1, 2)).ProviderIds.Clear();
        harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        harness.Entry(1099, "Unknown", Status.Completed, 12);
        var operations = new OperationStore(Plugin.Instance!.Database, TimeProvider.System);
        Guid id = Guid.NewGuid();
        operations.Create(id, harness.UserId, OperationKind.SyncFromTrackers, "x");
        var context = new OperationContext(id, harness.UserId, operations, _logs.CreateLogger("operation"), default);

        await harness.NewSync(loggers: _logs).SyncFromProvider(harness.UserId.ToString(), context);

        Assert.That(_logs.At(LogLevel.Information), Is.EqualTo(new[] {
            "(AniList) Unknown maps to no Jellyfin season; no AniDB ID is known for tracker entry 1099",
            $"(AniList) Marked 4 episodes of Alpha season 1 as played for user {harness.UserId}"
        }));
        Assert.That(_logs.At(LogLevel.Warning), Is.Empty);
        Assert.That(_logs.At(LogLevel.Error), Is.Empty);
    }
}
