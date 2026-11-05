using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;
using Match = jellyfin_anidoki.Matching.Match;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture, NonParallelizable]
public class ResolutionCheckTests {
    private SyncHarness _harness = null!;
    private Guid _user;
    private MatchStore _matches = null!;
    private ResolutionCheckStore _rows = null!;
    private OperationStore _operations = null!;
    private Mock<IAsyncDelayer> _delayer = null!;
    private CancellationTokenSource _cancellation = null!;

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _harness.ConfigureUser(_user, false, false, ApiName.AniList);
        var entry = SyncHarness.Entry(600, "Fixture Show", 12, Watching(3));
        FakeTracker tracker = _harness.Tracker(ApiName.AniList);
        tracker.Add(entry);
        tracker.SearchResults.Add(entry);
        tracker.Add(SyncHarness.Entry(A, "Entry 101", 12, Watching(0)));
        _matches = new MatchStore(Plugin.Instance!.Database, TimeProvider.System);
        _rows = new ResolutionCheckStore(Plugin.Instance.Database);
        _operations = new OperationStore(Plugin.Instance.Database, TimeProvider.System);
        _delayer = new Mock<IAsyncDelayer>();
        _cancellation = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown() {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    private async Task<(OperationContext Context, IReadOnlyList<ResolutionCheckRow> Rows)> Check(ISeasonWatchState watch, params Video[] targets) {
        Guid id = Guid.NewGuid();
        _operations.Create(id, _user, OperationKind.ResolutionCheck, "x");
        var context = new OperationContext(id, _user, _operations, NullLogger.Instance, _cancellation.Token);
        var check = new ResolutionCheck(new Mock<IUserManager>().Object, _harness.Library.Library.Object, new Mock<IUserDataManager>().Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), new Mock<IApplicationPaths>().Object,
            new MemoryCache(new MemoryCacheOptions()), _delayer.Object, _matches, _rows, watch, _user) {
            ClientOverride = (provider, _) => _harness.Tracker(provider)
        };
        await check.CheckTargets(targets, context);
        return (context, _rows.Read(id));
    }

    private void SaveFor(Episode episode, params MatchRange[] ranges) {
        SyncSource source = SyncSource.From(episode)!;
        _matches.Save(MatchSource.From(source), new Match(new MatchScope(ApiName.AniList, source.SourceKey), ranges, []), 0, "admin");
    }

    [Test]
    public async Task ItNeverWritesAndFindsNoDifferenceWithoutASavedMatch() {
        var (context, rows) = await Check(new FixedWatchState(1, 2, 3, 4, 5), _harness.Library.Episode("Fixture Show", 1, 5));

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        ResolutionCheckRow row = rows.Single();
        Assert.That(row.Provider, Is.EqualTo(ApiName.AniList));
        Assert.That(row.Automatic.Kind, Is.EqualTo("Resolved"));
        Assert.That(row.Automatic.Targets.Single().Id, Is.EqualTo(600));
        Assert.That(row.Saved, Is.Null);
        Assert.That(row.Differs, Is.False);
        Assert.That(context.Result, Is.EqualTo(OperationState.Succeeded));
        Assert.That(context.Outcomes, Is.Empty);
    }

    [Test]
    public async Task TheTwoProgressRulesDifferWhenALaterEpisodeWasAlreadyWatched() {
        var (_, rows) = await Check(new FixedWatchState(1, 2, 3, 4, 5, 6, 7, 8), _harness.Library.Episode("Fixture Show", 1, 5));

        Assert.That(rows.Single().PlayedRuleProgress, Is.EqualTo(5));
        Assert.That(rows.Single().HighestWatchedProgress, Is.EqualTo(8));
    }

    [Test]
    public async Task TheTwoProgressRulesAgreeWhenTheLatestWatchedWasPlayed() {
        var (_, rows) = await Check(new FixedWatchState(1, 2, 3, 4, 5), _harness.Library.Episode("Fixture Show", 1, 5));

        Assert.That(rows.Single().PlayedRuleProgress, Is.EqualTo(5));
        Assert.That(rows.Single().HighestWatchedProgress, Is.EqualTo(5));
    }

    [TestCase(Status.Completed, 12, false)]
    [TestCase(Status.Rewatching, 3, true)]
    public async Task TheTwoProgressRulesAgreeOnAnEntryNotBehind(Status status, int progress, bool rewatching) {
        var entry = SyncHarness.Entry(700, "Finished Show", 12, new MyListStatus { Status = status, NumEpisodesWatched = progress, IsRewatching = rewatching, RewatchCount = 1 });
        _harness.Tracker(ApiName.AniList).Add(entry);
        _harness.Tracker(ApiName.AniList).SearchResults.Add(entry);

        var (_, rows) = await Check(new FixedWatchState(1, 2, 3, 4, 5, 6, 7, 8), _harness.Library.Episode("Finished Show", 1, 5));

        Assert.That(rows.Single().PlayedRuleProgress, Is.EqualTo(5));
        Assert.That(rows.Single().HighestWatchedProgress, Is.EqualTo(5));
    }

    [Test]
    public async Task ASavedMatchPointingElsewhereDiffers() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, Range(1, 12, A));

        var (context, rows) = await Check(new FixedWatchState(1, 2, 3, 4, 5, 6), episode);

        ResolutionCheckRow row = rows.Single();
        Assert.That(row.Differs, Is.True);
        Assert.That(row.Automatic.Targets.Single().Id, Is.EqualTo(600));
        Assert.That(row.Saved!.By, Is.EqualTo(ResolvedBy.SharedMatch));
        Assert.That(row.Saved.Targets.Single().Id, Is.EqualTo(A));
        Assert.That(row.PlayedRuleProgress, Is.EqualTo(5));
        Assert.That(row.HighestWatchedProgress, Is.EqualTo(6));
        Assert.That(context.Message, Does.Contain("Saved answers differing from automatic: 1."));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
    }

    [Test]
    public async Task SavingTheAutomaticAnswerDoesNotDiffer() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, new MatchRange(1, 12, new MatchTarget(Ref(600), "An older title"), 1));

        var (_, rows) = await Check(new FixedWatchState(5), episode);

        Assert.That(rows.Single().Saved, Is.Not.Null);
        Assert.That(rows.Single().Differs, Is.False);
    }

    [Test]
    public async Task ASavedMatchTheEpisodeIsOutsideOfDiffers() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, Range(10, 12, A));

        var (_, rows) = await Check(new FixedWatchState(5), episode);

        Assert.That(rows.Single().Saved!.Kind, Is.EqualTo("Unresolved"));
        Assert.That(rows.Single().Saved!.Reason, Is.EqualTo("not-in-match"));
        Assert.That(rows.Single().PlayedRuleProgress, Is.Null);
        Assert.That(rows.Single().Differs, Is.True);
    }

    [Test]
    public async Task AFailedProviderGetsARowAndIsNotAskedAgainWhenRateLimited() {
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.RateLimited;

        var (context, rows) = await Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Other Show", 1, 1));

        ResolutionCheckRow row = rows.Single();
        Assert.That(row.Automatic.Kind, Is.EqualTo("Failed"));
        Assert.That(row.Automatic.Reason, Is.EqualTo("RateLimited"));
        Assert.That(context.Result, Is.EqualTo(OperationState.Succeeded));
    }

    [Test]
    public async Task ATransientFailureOnlyCostsThatSeason() {
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.Transient;

        var (_, rows) = await Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Other Show", 1, 1));

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EachLinkedProviderGetsARow() {
        _harness.ConfigureUser(_user, false, false, ApiName.AniList, ApiName.Kitsu);
        var entry = SyncHarness.Entry(600, "Fixture Show", 12, null);
        _harness.Tracker(ApiName.Kitsu).Add(entry);
        _harness.Tracker(ApiName.Kitsu).SearchResults.Add(entry);

        var (_, rows) = await Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1));

        Assert.That(rows.Select(row => row.Provider), Is.EqualTo(new[] { ApiName.AniList, ApiName.Kitsu }));
    }

    [Test]
    public async Task ItReportsProgressAndWaitsBetweenSeasons() {
        var (_, rows) = await Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1), _harness.Library.Episode("Fixture Show", 2, 1));

        Assert.That(rows, Has.Count.EqualTo(2));
        _delayer.Verify(delayer => delayer.Delay(It.IsAny<TimeSpan>()), Times.Once);
    }

    [Test]
    public void CancellingEndsTheRunWithAMessage() {
        _cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1)));
    }

    [Test]
    public async Task AUserWithNoLinksFindsNothingToCheck() {
        _harness.Configuration.UserConfig = [];

        var (context, rows) = await Check(new FixedWatchState(1), _harness.Library.Episode("Fixture Show", 1, 1));

        Assert.That(rows, Is.Empty);
        Assert.That(context.Result, Is.EqualTo(OperationState.NoChange));
    }

    [Test]
    public void TheMostRecentlyPlayedEpisodeIsTheOneChecked() {
        var early = new Episode { IndexNumber = 8 };
        var late = new Episode { IndexNumber = 5 };

        Video? chosen = ResolutionCheck.MostRecentlyPlayed([(early, new DateTime(2026, 1, 1)), (late, new DateTime(2026, 2, 1))]);

        Assert.That(chosen, Is.SameAs(late));
    }

    [Test]
    public void AHighestEpisodeStandsInWhenDatesTie() {
        var low = new Episode { IndexNumber = 2 };
        var high = new Episode { IndexNumber = 9 };

        Assert.That(ResolutionCheck.MostRecentlyPlayed([(low, null), (high, null)]), Is.SameAs(high));
    }

    [Test]
    public void NothingPlayedChoosesNothing() {
        Assert.That(ResolutionCheck.MostRecentlyPlayed([]), Is.Null);
    }

    [Test]
    public void RowsRoundTripThroughTheStore() {
        var row = new ResolutionCheckRow("key", ApiName.Kitsu,
            new CheckResolution("Resolved", ResolvedBy.Ids, null, [new CheckTarget(1, "alt", "Title", 3, TargetRole.Played)]),
            CheckResolution.Failed(TrackerFailureKind.Transient), 3, 4, true);
        Guid id = Guid.NewGuid();

        _rows.Add(id, 0, row);

        ResolutionCheckRow read = _rows.Read(id).Single();
        Assert.That(read.SourceKey, Is.EqualTo("key"));
        Assert.That(read.Automatic.Targets, Is.EqualTo(row.Automatic.Targets));
        Assert.That(read.Saved!.Reason, Is.EqualTo("Transient"));
        Assert.That((read.PlayedRuleProgress, read.HighestWatchedProgress, read.Differs), Is.EqualTo((3, 4, true)));
    }
}
