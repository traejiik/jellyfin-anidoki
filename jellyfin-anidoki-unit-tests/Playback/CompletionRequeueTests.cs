using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>A watch update cut off by a restart is enqueued once more at startup, and re-running it writes nothing twice.</summary>
[TestFixture, NonParallelizable]
public class CompletionRequeueTests {
    private string _directory = null!;
    private SyncHarness _harness = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;
    private CompletionLedger _ledger = null!;
    private Episode _episode = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _harness = new SyncHarness();
        (_store, _runner) = Create(_directory);
        _ledger = Ledger(_directory);
        _harness.ConfigureUser(_user, planToWatchOnly: false, rewatchCompleted: false, ApiName.AniList);
        // The interrupted run already wrote episode 5 before Jellyfin stopped.
        _harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12,
            new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 5 }));
        _episode = _harness.Library.Episode("Fixture Show", 1, 5, new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        _harness.Library.Library.Setup(manager => manager.GetItemById(_episode.Id)).Returns(_episode);
    }

    [TearDown]
    public async Task TearDown() {
        try {
            await _runner.Drain().WaitAsync(TimeSpan.FromSeconds(10));
        } catch (TimeoutException) {
        }
        _runner.Dispose();
        _harness.Dispose();
        Directory.Delete(_directory, true);
    }

    private CompletionRequeue Requeue() => new(_ledger, _runner, Replay(_harness), _harness.Library.Library.Object, NullLoggerFactory.Instance);

    /// <summary>The replay the requeue and the retry worker share, against the harness's fake trackers.</summary>
    internal static TriggerReplay Replay(SyncHarness harness) => new(NullLoggerFactory.Instance, harness.Http, harness.Library.Library.Object,
        new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), IdServices.Arm(harness.Http, harness.Configuration),
        IdServices.AnimeList(harness.Http, configuration: harness.Configuration), new MemoryCache(new MemoryCacheOptions())) {
        ClientOverride = (provider, _) => harness.Tracker(provider)
    };

    /// <summary>Leaves a recorded completion whose Playback operation was running when Jellyfin stopped, then starts again.</summary>
    private async Task<Guid> RestartDuringAPlaybackUpdate(BaseItem item) {
        Guid interrupted = Guid.NewGuid();
        _store.Create(interrupted, _user, OperationKind.Playback, OperationSources.Describe(item));
        _store.MarkRunning(interrupted);
        Assert.That(_ledger.TryRecord(_user, item.Id, "session-1", CompletionTrigger.Stop, interrupted), Is.True);
        await _runner.StartAsync(CancellationToken.None);
        await _runner.StopAsync(CancellationToken.None);
        _runner.Dispose();
        (_store, _runner) = Create(_directory);
        return interrupted;
    }

    [Test]
    public async Task AnInterruptedPlaybackUpdateRunsOnceMoreAndWritesNothingTwice() {
        Guid interrupted = await RestartDuringAPlaybackUpdate(_episode);
        int writes = _harness.Tracker(ApiName.AniList).Writes.Count;

        await Requeue().StartAsync(CancellationToken.None);
        await WaitUntil(() => _store.ReadForUser(_user).Count(view => OperationStates.IsFinished(view.State)) == 2);
        await _runner.Drain();

        var operations = _store.ReadForUser(_user);
        Assert.That(operations.Single(view => view.OperationId == interrupted).State, Is.EqualTo(OperationState.Interrupted));
        var requeued = operations.Single(view => view.OperationId != interrupted);
        Assert.That(requeued.Kind, Is.EqualTo(OperationKind.Playback));
        Assert.That(requeued.State, Is.EqualTo(OperationState.NoChange));
        Assert.That(requeued.Counts, Is.EqualTo(new Dictionary<string, int> { ["NoChange"] = 1 }));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Has.Count.EqualTo(writes));
        Assert.That(_ledger.Interrupted(), Is.Empty);
    }

    [Test]
    public async Task ARequeuedUpdateIsNeverRequeuedASecondTime() {
        await RestartDuringAPlaybackUpdate(_episode);
        var completion = _ledger.Interrupted().Single();
        // The requeued run was itself cut off by another restart.
        Guid second = Guid.NewGuid();
        _store.Create(second, _user, OperationKind.Playback, OperationSources.Describe(_episode));
        _ledger.MarkRequeued(completion.Id, second);
        _store.MarkInterrupted();

        await Requeue().StartAsync(CancellationToken.None);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(_user), Has.Count.EqualTo(2));
        Assert.That(_ledger.Interrupted(), Is.Empty);
    }

    [Test]
    public async Task AnUpdateForAnItemThatIsGoneIsNotRequeued() {
        var gone = _harness.Library.Episode("Fixture Show", 1, 6);
        await RestartDuringAPlaybackUpdate(gone);

        await Requeue().StartAsync(CancellationToken.None);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(_user), Has.Count.EqualTo(1));
        Assert.That(_ledger.Interrupted(), Is.Empty);
    }
}
