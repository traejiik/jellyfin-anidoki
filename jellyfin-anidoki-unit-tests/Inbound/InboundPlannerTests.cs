using System;
using System.Linq;
using jellyfin_anidoki;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities.TV;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Inbound;

[TestFixture, NonParallelizable]
public class InboundPlannerTests {
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private InboundHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new InboundHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private static jellyfin_anidoki.Sync.SyncAnimeMetadata Found(int episodesWatched, DateTime? completedAt = null) =>
        new() { episodesWatched = episodesWatched, season = 1, completedAt = completedAt, entry = new Anime { Id = 1, Title = "Show" } };

    private static int?[] Numbers(System.Collections.Generic.IEnumerable<Episode> episodes) => episodes.Select(episode => episode.IndexNumber).ToArray();

    [Test]
    public void EpisodesUpToTheTrackerProgressAreMarked() {
        Season season = InboundHarness.SeasonOf(_harness.Series("Show", 1, (1, 6)), 1);

        InboundSeasonPlan plan = InboundPlanner.ForSeason(season, Found(4), Now);

        Assert.That(Numbers(plan.Episodes), Is.EqualTo(new int?[] { 1, 2, 3, 4 }));
        Assert.That(plan.MarksSeason, Is.True);
    }

    [Test]
    public void NoProgressMarksEveryNumberedEpisode() {
        var series = _harness.Series("Show", 1, (1, 3));
        Season season = InboundHarness.SeasonOf(series, 1);
        season.Children = season.Children.Append(new Episode { Id = Guid.NewGuid(), Name = "Unnumbered", ParentId = season.Id }).ToList();

        InboundSeasonPlan plan = InboundPlanner.ForSeason(season, Found(-1), Now);

        Assert.That(Numbers(plan.Episodes), Is.EqualTo(new int?[] { 1, 2, 3 }));
    }

    [Test]
    public void AnIgnoredEpisodeIsHeldBackAndKeepsTheSeasonUnmarked() {
        var series = _harness.Series("Show", 1, (1, 3));
        InboundHarness.Ignore(InboundHarness.EpisodeOf(series, 1, 2));

        InboundSeasonPlan plan = InboundPlanner.ForSeason(InboundHarness.SeasonOf(series, 1), Found(3), Now);

        Assert.That(Numbers(plan.Episodes), Is.EqualTo(new int?[] { 1, 3 }));
        Assert.That(Numbers(plan.IgnoredEpisodes), Is.EqualTo(new int?[] { 2 }));
        Assert.That(plan.MarksSeason, Is.False);
    }

    [Test]
    public void AnIgnoredSeasonMarksNothing() {
        Season season = InboundHarness.SeasonOf(_harness.Series("Show", 1, (1, 3)), 1);
        InboundHarness.Ignore(season);

        InboundSeasonPlan plan = InboundPlanner.ForSeason(season, Found(3), Now);

        Assert.That(plan.SeasonIgnored, Is.True);
        Assert.That(plan.Episodes, Is.Empty);
        Assert.That(plan.MarksSeason, Is.False);
    }

    [Test]
    public void TheFinishDateIsThePlayedDate() {
        Season season = InboundHarness.SeasonOf(_harness.Series("Show", 1, (1, 1)), 1);
        var finished = new DateTime(2026, 1, 2);

        Assert.That(InboundPlanner.ForSeason(season, Found(1, finished), Now).PlayedDate, Is.EqualTo(finished));
        Assert.That(InboundPlanner.ForSeason(season, Found(1), Now).PlayedDate, Is.EqualTo(Now));
    }
}
