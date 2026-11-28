using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using jellyfin_anidoki_unit_tests.Trackers.AniList;
using jellyfin_anidoki_unit_tests.Trackers.Kitsu;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>
/// D6 end to end: a playback update that one tracker failed is retried later for that tracker alone, re-planned from the
/// tracker's current state, and the retry survives a restart.
/// </summary>
[TestFixture, NonParallelizable]
public class RetryWorkerTests {
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private SyncHarness _harness = null!;
    private MutableTime _clock = null!;
    private OperationStore _store = null!;
    private RetryQueue _retries = null!;
    private OperationRunner _runner = null!;
    private TriggerReplay _replay = null!;
    private Episode _episode = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _clock = new MutableTime(Start);
        _harness = new SyncHarness();
        _harness.ConfigureUser(_user, planToWatchOnly: false, rewatchCompleted: false, ApiName.AniList, ApiName.Kitsu);
        _harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 4 }));
        _episode = _harness.Library.Episode("Fixture Show", 1, 5);
        _harness.Library.Library.Setup(manager => manager.GetItemById(_episode.Id)).Returns(_episode);
        _replay = CompletionRequeueTests.Replay(_harness);
        Restart();
    }

    [TearDown]
    public async Task TearDown() {
        await _runner.Drain();
        _runner.Dispose();
        _harness.Dispose();
        Directory.Delete(_directory, true);
    }

    /// <summary>A new store, queue and runner on the same database file, as after a restart.</summary>
    private void Restart() {
        _runner?.Dispose();
        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _store = new OperationStore(database, _clock);
        _retries = new RetryQueue(database, _clock, item => RetryWorker.SeriesKey(_harness.Library.Library.Object, item));
        _runner = new OperationRunner(_store, NullLogger<OperationRunner>.Instance, _retries);
    }

    private RetryWorker Worker() => new(_retries, _runner, _replay, _harness.Library.Library.Object, NullLogger<RetryWorker>.Instance);

    /// <summary>Plays episode 5 to the end with AniList unreachable: Kitsu is written, AniList fails and gets a retry.</summary>
    private async Task PlayWithAniListDown() {
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.Transient;
        var handle = _runner.Enqueue(_user, OperationKind.Playback, OperationSources.Describe(_episode),
            operation => _replay.Run(_episode, _user, OperationKind.Playback, null, operation), retry: new RetrySubject(_episode.Id, OperationKind.Playback))!;
        await handle.Completion;
        _harness.Tracker(ApiName.AniList).ReadFailure = null;
    }

    private async Task<OperationView> RunTheRetry() {
        int started = Worker().RunDue();
        Assert.That(started, Is.EqualTo(1));
        await _runner.Drain();
        return _store.ReadForUser(_user).Single(view => view.Kind == OperationKind.Retry);
    }

    [Test]
    public async Task AFailedTrackerIsRetriedAloneAndLandsOnce() {
        await PlayWithAniListDown();
        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Has.Count.EqualTo(1));
        Assert.That(_retries.ForUser(_user).Single().Provider, Is.EqualTo(ApiName.AniList));

        Assert.That(Worker().RunDue(), Is.Zero, "not due before two minutes");
        _clock.Now = Start.AddMinutes(2);
        var retry = await RunTheRetry();

        Assert.That(retry.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(retry.RetryAttempt, Is.EqualTo(1));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Single().Progress, Is.EqualTo(5));
        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Has.Count.EqualTo(1), "the tracker that worked is not touched again");
        Assert.That(_retries.ForUser(_user), Is.Empty);
    }

    [Test]
    public async Task AStaleRetryReplansToNoChange() {
        await PlayWithAniListDown();
        // The user moved AniList on by hand while the retry waited.
        _harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12,
            new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 7 }));

        _clock.Now = Start.AddMinutes(2);
        var retry = await RunTheRetry();

        Assert.That(retry.State, Is.EqualTo(OperationState.NoChange));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(_retries.ForUser(_user), Is.Empty);
    }

    [Test]
    public async Task ARetryThatFailsAgainMovesToTheNextAttempt() {
        await PlayWithAniListDown();
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.RateLimited;

        _clock.Now = Start.AddMinutes(2);
        var retry = await RunTheRetry();

        Assert.That(retry.State, Is.EqualTo(OperationState.Failed));
        var row = _retries.ForUser(_user).Single();
        Assert.That(row.Attempt, Is.EqualTo(2));
        Assert.That(row.DueUtc, Is.EqualTo(Start.AddMinutes(12)));
        Assert.That(row.LastStep, Is.EqualTo("rate-limited"));
    }

    [Test]
    public async Task ARetryForAnItemThatIsGoneIsDropped() {
        await PlayWithAniListDown();
        _harness.Library.Library.Setup(manager => manager.GetItemById(_episode.Id)).Returns((Episode?)null);

        _clock.Now = Start.AddMinutes(2);
        Assert.That(Worker().RunDue(), Is.Zero);

        Assert.That(_retries.ForUser(_user), Is.Empty);
        Assert.That(_store.ReadForUser(_user).Any(view => view.Kind == OperationKind.Retry), Is.False);
    }

    [Test]
    public async Task ADueRetrySurvivesARestart() {
        await PlayWithAniListDown();
        await _runner.Drain();

        Restart();
        _clock.Now = Start.AddMinutes(5);
        var retry = await RunTheRetry();

        Assert.That(retry.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ARunningRetryIsNotStartedTwice() {
        await PlayWithAniListDown();
        _clock.Now = Start.AddMinutes(2);
        var worker = Worker();

        Assert.That(worker.RunDue(), Is.EqualTo(1));
        Assert.That(worker.RunDue(), Is.Zero, "the lease holds it back");
        await _runner.Drain();
    }
}
