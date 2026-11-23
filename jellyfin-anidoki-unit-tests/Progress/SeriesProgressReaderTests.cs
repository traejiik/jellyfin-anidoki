using System;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Matching;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Progress;

public class SeriesProgressReaderTests {
    private ProgressFixture _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new ProgressFixture();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task EachSeasonIsListedInOrderWithTheSeriesDetails() {
        SeriesViewContract view = (await _fixture.Read())!;

        Assert.That(view.Name, Is.EqualTo("Long Show"));
        Assert.That(view.Images, Is.EqualTo(new[] { "Primary", "Logo" }));
        Assert.That(view.EpisodeCount, Is.EqualTo(60));
        Assert.That(view.Seasons.Select(season => season.SourceKey), Is.EqualTo(_fixture.Series.SourceKeys));
        Assert.That(view.Trackers.Single().Provider, Is.EqualTo(ApiName.AniList));
    }

    [Test]
    public async Task ASeasonMatchedAsFourEntriesShowsEachWithItsEpisodesAndJellyfinProgress() {
        _fixture.Shared(_fixture.Match.Season, Range(1, 12, A), Range(13, 24, B), Range(25, 36, C), Range(37, 48, D));
        _fixture.Match.Catalogue.Add(A, 12, new ListState(TrackerStatus.Completed, 12, false, 0, 80));
        _fixture.Match.Catalogue.Add(B, 12, new ListState(TrackerStatus.Watching, 3, false, 0));
        _fixture.Match.WatchState.ByUser[_fixture.Alice] = Enumerable.Range(1, 17).ToArray();

        SeasonTrackerContract row = (await _fixture.Read())!.Seasons[0].Trackers.Single();

        Assert.That(row.Origin, Is.EqualTo(MatchOrigin.Shared));
        Assert.That(row.Entries.Select(entry => (entry.Id, entry.FirstEpisode, entry.LastEpisode, entry.JellyfinProgress)),
            Is.EqualTo(new[] { (A, (int?)1, (int?)12, 12), (B, 13, 24, 5), (C, 25, 36, 0), (D, 37, 48, 0) }));
        Assert.That(row.Entries[0].Score, Is.EqualTo(80));
        Assert.That(row.Entries[1].Status, Is.EqualTo(TrackerStatus.Watching));
        Assert.That(row.Entries[2].Status, Is.Null, "not on the list");
    }

    [Test]
    public async Task APersonalMatchWinsAndIsLabelledSo() {
        _fixture.Shared(_fixture.Match.Season, Range(1, 48, A));
        _fixture.Personal(_fixture.Match.Season, [Range(1, 24, B)]);

        SeasonTrackerContract row = (await _fixture.Read())!.Seasons[0].Trackers.Single();

        Assert.That(row.Origin, Is.EqualTo(MatchOrigin.Personal));
        Assert.That(row.Entries.Single().Id, Is.EqualTo(B));
    }

    [Test]
    public async Task WithSavedMatchesOffTheAutomaticAnswerIsShown() {
        _fixture.Shared(_fixture.Match.Season, Range(1, 48, A));
        _fixture.Match.Automatic.Answer = new(new jellyfin_anidoki.Matching.Match(new MatchScope(ApiName.AniList, _fixture.Match.Key), [Range(1, 48, C)], []), null);

        var view = await _fixture.Reader.Read(_fixture.Series.Id, _fixture.Alice, false, true, default);

        SeasonTrackerContract row = view!.Seasons[0].Trackers.Single();
        Assert.That(row.Origin, Is.EqualTo(MatchOrigin.Automatic));
        Assert.That(row.Entries.Single().Id, Is.EqualTo(C));
    }

    [Test]
    public async Task ASeasonAutomaticResolutionCannotPlaceSaysWhy() {
        _fixture.Match.Automatic.Answer = new(null, "no-ids");

        SeasonTrackerContract row = (await _fixture.Read())!.Seasons[0].Trackers.Single();

        Assert.That(row.Origin, Is.EqualTo(MatchOrigin.Automatic));
        Assert.That(row.Unresolved, Is.EqualTo("no-ids"));
        Assert.That(row.Entries, Is.Empty);
    }

    [Test]
    public async Task ATrackerWithoutScoresSaysSo() {
        var annict = new CatalogueFake(ApiName.Annict);
        _fixture.Match.Trackers.Link(_fixture.Alice, annict);

        TrackerAccountContract account = (await _fixture.Read())!.Trackers.Single(tracker => tracker.Provider == ApiName.Annict);

        Assert.That(account.SupportsScore, Is.False);
        Assert.That(account.ScoreFormat, Is.Null);
    }

    [Test]
    public async Task AFailingTrackerIsReportedWhileTheOthersStillShow() {
        _fixture.Shared(_fixture.Match.Season, Range(1, 48, A));
        _fixture.Match.Trackers.Link(_fixture.Alice, new FailingTracker(ApiName.Kitsu, TrackerFailureKind.RateLimited));
        // Kitsu has no saved match, so automatic resolution answers, and reading its entry fails.
        _fixture.Match.Automatic.Answer = new(new jellyfin_anidoki.Matching.Match(new MatchScope(ApiName.Kitsu, _fixture.Match.Key),
            [new MatchRange(1, 48, new MatchTarget(new EntryRef(ApiName.Kitsu, 7, null), "Kitsu entry"), 1)], []), null);

        SeasonViewContract season = (await _fixture.Read())!.Seasons[0];

        Assert.That(season.Trackers.Single(row => row.Provider == ApiName.Kitsu).Error, Is.EqualTo("RateLimited"));
        Assert.That(season.Trackers.Single(row => row.Provider == ApiName.AniList).Entries.Single().Id, Is.EqualTo(A));
    }

    [Test]
    public async Task AnItemTheUserCannotSeeIsNotFound() {
        var hidden = _fixture.Items.Add(_fixture.Series with { Id = Guid.NewGuid() }, _fixture.Match.Bob.Id);

        Assert.That(await _fixture.Read(hidden.Id), Is.Null);
    }

    [Test]
    public async Task ASeriesNoneOfWhoseSeasonsTheUserSyncsIsNotFound() {
        var hidden = _fixture.Items.Add(_fixture.Series with { Id = Guid.NewGuid(), SourceKeys = [_fixture.Match.Hidden.Info.SourceKey] });

        Assert.That(await _fixture.Read(hidden.Id), Is.Null);
        Assert.That(await _fixture.Read(hidden.Id, _fixture.Match.Bob.Id), Is.Not.Null);
    }

    [Test]
    public async Task TheDefaultSeasonIsTheOneOpenedFromElseTheFirst() {
        Assert.That((await _fixture.Read())!.DefaultSeason, Is.EqualTo(_fixture.Match.Key));

        var opened = _fixture.Items.Add(_fixture.Series with { Id = Guid.NewGuid(), OpenedSeason = 2 });
        // The fixture's second season is numbered 2 in its own key.
        Assert.That((await _fixture.Read(opened.Id))!.DefaultSeason, Is.EqualTo(_fixture.SeasonTwo.Info.SourceKey));
    }
}
