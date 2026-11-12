using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Matching;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;
using Match = jellyfin_anidoki.Matching.Match;

namespace jellyfin_anidoki_unit_tests.Preview;

/// <summary>The outbound preview through the real pipeline and executor, against a fake tracker. Nothing may be written.</summary>
[TestFixture, NonParallelizable]
public class OutboundPreviewTests {
    private const int Show = 600;

    private SyncHarness _harness = null!;
    private Guid _user;
    private MatchStore _matches = null!;
    private PreviewStore _rows = null!;
    private OperationStore _operations = null!;
    private Mock<IAsyncDelayer> _delayer = null!;
    private CancellationTokenSource _cancellation = null!;

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _harness.ConfigureUser(_user, false, false, ApiName.AniList);
        _matches = new MatchStore(Plugin.Instance!.Database, TimeProvider.System);
        _rows = new PreviewStore(Plugin.Instance.Database, TimeProvider.System);
        _operations = new OperationStore(Plugin.Instance.Database, TimeProvider.System);
        _delayer = new Mock<IAsyncDelayer>();
        _cancellation = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown() {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    /// <summary>The show's entry, found by title search as in the resolution check's tests.</summary>
    private void Seed(int episodes, MyListStatus? status, ApiName provider = ApiName.AniList) {
        var entry = SyncHarness.Entry(Show, "Fixture Show", episodes, status);
        FakeTracker tracker = _harness.Tracker(provider);
        tracker.Add(entry);
        tracker.SearchResults.Add(entry);
    }

    private void SaveFor(Episode episode, MatchRange[] ranges, params MatchException[] exceptions) {
        SyncSource source = SyncSource.From(episode)!;
        _matches.Save(MatchSource.From(source), new Match(new MatchScope(ApiName.AniList, source.SourceKey), ranges, exceptions), 0, "admin");
    }

    private OutboundPreview NewPreview(ISeasonWatchState watch, ApiName? only = null) =>
        new(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance, new Mock<IHttpClientFactory>().Object,
            new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), new Mock<IApplicationPaths>().Object,
            new MemoryCache(new MemoryCacheOptions()), _delayer.Object, _matches, _rows, watch, _user) {
            ClientOverride = (provider, _) => _harness.Tracker(provider),
            OnlyProvider = only
        };

    private OperationContext NewContext() {
        Guid id = Guid.NewGuid();
        _operations.Create(id, _user, OperationKind.PreviewToTrackers, "x");
        return new OperationContext(id, _user, _operations, NullLogger.Instance, _cancellation.Token);
    }

    private IReadOnlyList<PreviewRow> RowsOf(OperationContext context) => _rows.Page(context.OperationId, null, null, 0, PreviewStore.MaxPageSize).Rows;

    private async Task<(OperationContext Context, IReadOnlyList<PreviewRow> Rows)> RunPreview(ISeasonWatchState watch, ApiName? only,
        params Episode[] targets) {
        OperationContext context = NewContext();
        await NewPreview(watch, only).PreviewEpisodes(targets, context);
        return (context, RowsOf(context));
    }

    private void AssertNothingWritten() {
        foreach (ApiName provider in new[] { ApiName.AniList, ApiName.Kitsu })
            Assert.That(_harness.Tracker(provider).Writes, Is.Empty, provider.ToString());
    }

    [Test]
    public async Task AWriteIsListedAsASelectedChangeAndNothingIsWritten() {
        Seed(12, Watching(3));

        var (context, rows) = await RunPreview(new FixedWatchState(1, 2, 3, 4, 5), null, _harness.Library.Episode("Fixture Show", 1, 5));

        AssertNothingWritten();
        PreviewRow row = rows.Single();
        Assert.Multiple(() => {
            Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Change));
            Assert.That(row.Reason, Is.EqualTo("progress"));
            Assert.That(row.TargetId, Is.EqualTo(Show.ToString()));
            Assert.That(row.TargetEpisode, Is.EqualTo(5));
            Assert.That(row.PlannedChange!.Progress, Is.EqualTo(5));
            Assert.That(row.PlannedChange.Status, Is.EqualTo(TrackerStatus.Watching));
            Assert.That(row.PlannedAgainst!.List!.Progress, Is.EqualTo(3));
            Assert.That(row.PlannedAgainst.Titles, Is.Null);
            Assert.That(row.SourceTitle, Is.EqualTo("Fixture Show · season 1"));
            Assert.That(row.SourceEpisode, Is.EqualTo(5));
            Assert.That(row.WatchedFingerprint, Is.EqualTo("1-5"));
            Assert.That(row.MatchRevision, Is.Null);
            Assert.That(row.Selectable && row.SelectedByDefault, Is.True);
            Assert.That(context.Result, Is.EqualTo(OperationState.Succeeded));
            Assert.That(context.Message, Is.EqualTo("Previewed 1 season: 1 to change, 0 that would lower progress. Nothing was written."));
            Assert.That(context.Outcomes, Is.Empty);
        });
    }

    [Test]
    public async Task ATrackerAheadOfJellyfinIsOfferedAsAReductionThatIsNotSelected() {
        Seed(24, Watching(14));

        var (_, rows) = await RunPreview(new FixedWatchState(1, 2, 3, 4, 5, 6, 7, 8), null, _harness.Library.Episode("Fixture Show", 1, 8));

        AssertNothingWritten();
        PreviewRow row = rows.Single();
        Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Reduction));
        Assert.That(row.Reason, Is.EqualTo(jellyfin_anidoki.Planning.ReductionCheck.Reason));
        Assert.That(row.PlannedChange, Is.EqualTo(new EntryChange(8, TrackerStatus.Watching)));
        Assert.That(row.PlannedAgainst!.List!.Progress, Is.EqualTo(14));
        Assert.That(row.Selectable, Is.True);
        Assert.That(row.SelectedByDefault, Is.False);
    }

    [Test]
    public async Task AnEntryAlreadyThereIsNoChange() {
        Seed(12, Watching(5));

        var (_, rows) = await RunPreview(new FixedWatchState(1, 2, 3, 4, 5), null, _harness.Library.Episode("Fixture Show", 1, 5));

        PreviewRow row = rows.Single();
        Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.NoChange));
        Assert.That(row.Reason, Is.EqualTo("already-watched"));
        Assert.That(row.PlannedChange, Is.Null);
        Assert.That(row.Selectable, Is.False);
    }

    [Test]
    public async Task EachEntryOfASeasonMatchedAsFourTimesTwelveGetsItsOwnRow() {
        _harness.Configuration.applySavedMatches = true;
        foreach (int id in new[] { A, B, C, D })
            _harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(id, $"Entry {id}", 12, Watching(0)));
        Episode episode = _harness.Library.Episode("Long Show", 1, 48);
        SaveFor(episode, FourTimesTwelve().Ranges.ToArray());

        var (_, rows) = await RunPreview(new FixedWatchState([.. Enumerable.Range(1, 14), 20, 30, 48]), null, episode);

        AssertNothingWritten();
        Assert.That(rows.Select(row => (row.TargetId, row.TargetEpisode, row.Kind)), Is.EqualTo(new[] {
            (A.ToString(), (int?)12, PreviewRowKind.Change),
            (B.ToString(), (int?)8, PreviewRowKind.Change),
            (C.ToString(), (int?)6, PreviewRowKind.Change),
            (D.ToString(), (int?)12, PreviewRowKind.Change)
        }));
        Assert.That(rows.Select(row => row.MatchRevision), Is.All.EqualTo(1));
        Assert.That(rows.Select(row => row.ResolvedBy), Is.All.EqualTo(ResolvedBy.SharedMatch));
        Assert.That(rows.Select(row => row.ApplySavedMatches), Is.All.True);
        Assert.That(rows.Select(row => row.WatchedFingerprint), Is.All.EqualTo("1-14,20,30,48"));
    }

    [Test]
    public async Task AnIgnoredEpisodeIsListedAsSkipped() {
        _harness.Configuration.applySavedMatches = true;
        _harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(A, "Entry 101", 12, Watching(0)));
        Episode episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, [Range(1, 12, A)], Skip(5));

        var (_, rows) = await RunPreview(new FixedWatchState(5), null, episode);

        PreviewRow row = rows.Single();
        Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Skipped));
        Assert.That(row.Reason, Is.EqualTo("match-ignored"));
        Assert.That(row.MatchRevision, Is.EqualTo(1));
        Assert.That(row.Selectable, Is.False);
    }

    [Test]
    public async Task ARateLimitedTrackerIsNotAskedAgainButTheWaitIsKept() {
        Seed(12, Watching(3));
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.RateLimited;

        var (context, rows) = await RunPreview(new FixedWatchState(1), null,
            _harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Other Show", 1, 1));

        PreviewRow row = rows.Single();
        Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Failed));
        Assert.That(row.Reason, Is.EqualTo("rate-limited"));
        _delayer.Verify(delayer => delayer.Delay(TimeSpan.FromSeconds(2)), Times.Once);
        Assert.That(context.Result, Is.EqualTo(OperationState.Succeeded));
    }

    [Test]
    public async Task ATransientFailureOnlyCostsThatSeason() {
        Seed(12, Watching(3));
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.Transient;

        var (_, rows) = await RunPreview(new FixedWatchState(1), null,
            _harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Other Show", 1, 1));

        Assert.That(rows.Select(row => (row.Kind, row.Reason)), Is.All.EqualTo((PreviewRowKind.Failed, "transient")));
        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task OnlyTheChosenTrackerIsPreviewed() {
        _harness.ConfigureUser(_user, false, false, ApiName.AniList, ApiName.Kitsu);
        Seed(12, Watching(3));
        Seed(12, Watching(3), ApiName.Kitsu);

        var (_, rows) = await RunPreview(new FixedWatchState(1, 2, 3, 4, 5), ApiName.Kitsu, _harness.Library.Episode("Fixture Show", 1, 5));

        Assert.That(rows.Single().Provider, Is.EqualTo(ApiName.Kitsu));
        Assert.That(_harness.Tracker(ApiName.AniList).Reads, Is.Empty);
        AssertNothingWritten();
    }

    [Test]
    public void CancellingKeepsTheRowsFoundSoFar() {
        Seed(12, Watching(3));
        _delayer.Setup(delayer => delayer.Delay(It.IsAny<TimeSpan>())).Callback(() => _cancellation.Cancel()).Returns(Task.CompletedTask);
        OperationContext context = NewContext();

        Assert.ThrowsAsync<OperationCanceledException>(() => NewPreview(new FixedWatchState(1)).PreviewEpisodes(
            [_harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Other Show", 1, 1)], context));

        Assert.That(RowsOf(context), Has.Count.EqualTo(1));
        Assert.That(context.Message, Does.StartWith("Cancelled after 1 of 2 seasons."));
    }
}
