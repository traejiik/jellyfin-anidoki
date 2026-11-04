using System.Collections.Generic;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture, NonParallelizable]
public class MatchSourceTests {
    private LibraryFixture _library = null!;

    [SetUp]
    public void SetUp() => _library = new LibraryFixture();

    [Test]
    public void ASeasonTakesItsKeyTitleAndBothLevelsOfIds() {
        var episode = _library.Episode("Show", 2, 5, new Dictionary<string, string> { ["AniList"] = "10", ["Tvdb"] = "20" });
        episode.Season.ProviderIds["AniDB"] = "30";
        episode.Season.ProviderIds["Shoko Series"] = "40";
        var syncSource = SyncSource.From(episode)!;

        MatchSource source = MatchSource.From(syncSource);

        Assert.That(source.SourceKey, Is.EqualTo(syncSource.SourceKey));
        Assert.That(source.Kind, Is.EqualTo(MatchSourceKind.Season));
        Assert.That(source.Title, Is.EqualTo("Show · season 2"));
        Assert.That(source.Fingerprint, Is.EqualTo(new Dictionary<string, string> {
            ["series:AniList"] = "10",
            ["series:Tvdb"] = "20",
            ["season:AniDB"] = "30",
            ["season:Shoko Series"] = "40"
        }));
    }

    [Test]
    public void AMovieTakesItsOwnIds() {
        var movie = _library.Movie("Film", new Dictionary<string, string> { ["Tmdb"] = "50" });

        MatchSource source = MatchSource.From(SyncSource.From(movie)!);

        Assert.That(source.SourceKey, Is.EqualTo($"movie:{movie.Id:N}"));
        Assert.That(source.Kind, Is.EqualTo(MatchSourceKind.Movie));
        Assert.That(source.Title, Is.EqualTo("Film"));
        Assert.That(source.Fingerprint, Is.EqualTo(new Dictionary<string, string> { ["movie:Tmdb"] = "50" }));
    }

    [Test]
    public void OtherIdsAreLeftOut() {
        var movie = _library.Movie("Film", new Dictionary<string, string> { ["Imdb"] = "tt1", ["anidb"] = "60" });

        Assert.That(MatchSource.From(SyncSource.From(movie)!).Fingerprint, Is.EqualTo(new Dictionary<string, string> { ["movie:anidb"] = "60" }));
    }
}
