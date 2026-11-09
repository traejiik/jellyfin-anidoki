using System;
using System.IO;
using jellyfin_anidoki.Helpers;
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

    [Test]
    public void ANewerListIsReadAgain() {
        Write("""<anime anidbid="5975" tmdbid="12429" />""", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var index = new AnimeListIndex(() => _file);
        Assert.That(index.AniDbIdForMovie("12429"), Is.EqualTo(5975));

        Write("""<anime anidbid="9000" tmdbid="12429" />""", new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(index.AniDbIdForMovie("12429"), Is.EqualTo(9000));
    }
}
