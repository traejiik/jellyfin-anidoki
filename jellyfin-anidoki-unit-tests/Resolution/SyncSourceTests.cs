using System;
using System.Collections.Generic;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Resolution;

[TestFixture, NonParallelizable]
public class SyncSourceTests {
    private LibraryFixture _library = null!;

    [SetUp]
    public void SetUp() => _library = new LibraryFixture();

    [Test]
    public void EpisodeCarriesNumbersTitlesAndName() {
        var episode = _library.Episode("Show", 2, 5, episodeName: "Ep");
        episode.Series.OriginalTitle = "ショー";

        var source = SyncSource.From(episode)!;

        Assert.That(source.Item, Is.SameAs(episode));
        Assert.That(source.Kind, Is.EqualTo(MediaKind.Series));
        Assert.That(source.SeasonNumber, Is.EqualTo(2));
        Assert.That(source.EpisodeNumber, Is.EqualTo(5));
        Assert.That(source.SearchTitle, Is.EqualTo("Show"));
        Assert.That(source.CandidateTitles, Is.EqualTo(new[] { "Show", "ショー" }));
        Assert.That(source.EpisodeName, Is.EqualTo("Ep"));
    }

    [Test]
    public void MovieIsOneEpisodeOfSeasonOne() {
        var movie = _library.Movie("Film");
        movie.OriginalTitle = "映画";

        var source = SyncSource.From(movie)!;

        Assert.That(source.Kind, Is.EqualTo(MediaKind.Movie));
        Assert.That(source.SeasonNumber, Is.EqualTo(1));
        Assert.That(source.EpisodeNumber, Is.EqualTo(1));
        Assert.That(source.SearchTitle, Is.EqualTo("Film"));
        Assert.That(source.CandidateTitles, Is.EqualTo(new[] { "Film", "映画" }));
        Assert.That(source.EpisodeName, Is.Null);
    }

    [Test]
    public void MissingEpisodeNumberGivesNoSource() =>
        Assert.That(SyncSource.From(_library.Episode("Show", 1, null)), Is.Null);

    [Test]
    public void FallbackAniListIdOnlyForSeasonOne() {
        var ids = new Dictionary<string, string> { ["AniList"] = "42" };
        Assert.That(SyncSource.From(_library.Episode("Show", 1, 1, ids))!.FallbackAniListId, Is.EqualTo(42));
        Assert.That(SyncSource.From(_library.Episode("Show", 2, 1, ids))!.FallbackAniListId, Is.Null);
    }

    [Test]
    public void MovieFallbackAniListId() =>
        Assert.That(SyncSource.From(_library.Movie("Film", new Dictionary<string, string> { ["AniList"] = "42" }))!.FallbackAniListId, Is.EqualTo(42));

    [Test]
    public void UnparsableAniListIdIsNoFallback() =>
        Assert.That(SyncSource.From(_library.Episode("Show", 1, 1, new Dictionary<string, string> { ["AniList"] = "x" }))!.FallbackAniListId, Is.Null);

    // Today a null Series throws out of the update; the source treats it as an item without IDs (decision 9).
    [Test]
    public void EpisodeWithoutSeriesHasNoIds() {
        var episode = _library.Episode("Show", 1, 1, new Dictionary<string, string> { ["AniList"] = "42" });
        episode.SeriesId = Guid.NewGuid();
        Assume.That(episode.Series, Is.Null);

        SyncSource? source = null;
        Assert.DoesNotThrow(() => source = SyncSource.From(episode));

        Assert.That(source!.FallbackAniListId, Is.Null);
        Assert.That(source.CandidateTitles, Is.EqualTo(new[] { "Show" }));
    }
}
