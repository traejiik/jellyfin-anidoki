using System;
using System.IO;
using jellyfin_anidoki.AnimeIds;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>The anime list's TMDB movie IDs, which tell an anime movie apart in a Mixed library.</summary>
public class AnimeListIndexTests {
    private string _directory = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-anime-list-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "anime-list-full.xml");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private void Write(string rows, DateTime? written = null) {
        File.WriteAllText(_file, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<anime-list>\n{rows}\n</anime-list>");
        if (written is { } at)
            File.SetLastWriteTimeUtc(_file, at);
    }

    [Test]
    public void AMovieTheListMapsGivesItsAniDbId() {
        Write("""
            <anime anidbid="23" tvdbid="76885" defaulttvdbseason="1" />
            <anime anidbid="5975" tvdbid="movie" tmdbid="12429" />
            <anime anidbid="8" tmdbid="1, 2" />
            <anime anidbid="not-a-number" tmdbid="77" />
            """);
        var index = new AnimeListIndex(() => _file);

        Assert.That(index.AniDbIdForMovie("12429"), Is.EqualTo(5975));
        Assert.That(index.AniDbIdForMovie("2"), Is.EqualTo(8), "a comma list maps each ID");
        Assert.That(index.AniDbIdForMovie("76885"), Is.Null, "a TVDB ID is not a TMDB movie ID");
        Assert.That(index.AniDbIdForMovie("77"), Is.Null, "a row without a usable AniDB ID maps nothing");
        Assert.That(index.AniDbIdForMovie("603"), Is.Null, "a movie the list does not know is not anime");
        Assert.That(index.AniDbIdForMovie(null), Is.Null);
    }

    [Test]
    public void AMissingOrUnreadableListKnowsNoMovie() {
        Assert.That(new AnimeListIndex(() => _file).AniDbIdForMovie("12429"), Is.Null);
        Assert.That(new AnimeListIndex(() => null).AniDbIdForMovie("12429"), Is.Null);
        Assert.That(new AnimeListIndex(() => throw new ArgumentNullException("path")).AniDbIdForMovie("12429"), Is.Null);
        File.WriteAllText(_file, "<anime-list><anime anidbid=");
        Assert.That(new AnimeListIndex(() => _file).AniDbIdForMovie("12429"), Is.Null);
    }

    private const string Movies = """
        <anime anidbid="5975" tvdbid="movie" tmdbid="12429" imdbid="tt0876563" />
        <anime anidbid="313" tvdbid="75534" defaulttvdbseason="0" tmdbtv="31713" imdbid="tt0114563" />
        <anime anidbid="400" tvdbid="movie" tmdbid="40, 41" imdbid="tt4000000,tt4100000" />
        <anime anidbid="401" tvdbid="movie" tmdbid="41" />
        <anime anidbid="23" tvdbid="76885" defaulttvdbseason="1" />
        """;

    [TestCase(5975, "12429", null, MovieAniDbVerdict.Agrees, 5975, TestName = "The entry lists the movie's TMDB ID")]
    [TestCase(313, null, "tt0114563", MovieAniDbVerdict.Agrees, 313, TestName = "The entry lists the movie's IMDb ID")]
    [TestCase(400, null, "tt4100000", MovieAniDbVerdict.Agrees, 400, TestName = "A comma list of IMDb IDs is matched by each")]
    [TestCase(401, "41", null, MovieAniDbVerdict.Agrees, 401, TestName = "Two entries listing one TMDB ID both agree")]
    [TestCase(313, "12429", null, MovieAniDbVerdict.Replaced, 5975, TestName = "The movie's TMDB ID maps to another entry")]
    [TestCase(313, "603", "tt2000000", MovieAniDbVerdict.Conflicts, null, TestName = "The entry's IMDb ID is another movie's")]
    [TestCase(5975, "603", null, MovieAniDbVerdict.Conflicts, null, TestName = "The entry's TMDB ID is another movie's")]
    [TestCase(313, "603", null, MovieAniDbVerdict.Unconfirmed, null, TestName = "A series TMDB ID is not compared with a movie's")]
    [TestCase(23, "603", "tt2000000", MovieAniDbVerdict.Unconfirmed, null, TestName = "An entry with no TMDB or IMDb ID")]
    [TestCase(14176, "38757", "tt0398286", MovieAniDbVerdict.NotListed, null, TestName = "The list has no row for the entry")]
    public void AMovieAniDbIdIsCheckedAgainstTheList(int aniDbId, string? tmdbId, string? imdbId, MovieAniDbVerdict verdict, int? expectedId) {
        Write(Movies);

        Assert.That(new AnimeListIndex(() => _file).CheckMovie(aniDbId, tmdbId, imdbId), Is.EqualTo(new MovieAniDbCheck(verdict, expectedId)));
    }

    [Test]
    public void WithNoListNoMovieAniDbIdIsListed() {
        Assert.That(new AnimeListIndex(() => null).CheckMovie(313, "603", "tt0114563").Verdict, Is.EqualTo(MovieAniDbVerdict.NotListed));
    }

    [Test]
    public void ANewerListIsReadAgain() {
        Write("""<anime anidbid="5975" tmdbid="12429" />""", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var index = new AnimeListIndex(() => _file);
        Assert.That(index.AniDbIdForMovie("12429"), Is.EqualTo(5975));

        Write("""<anime anidbid="9000" tmdbid="12429" />""", new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(index.AniDbIdForMovie("12429"), Is.EqualTo(9000));
    }
}
