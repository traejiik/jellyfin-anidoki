using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture, NonParallelizable]
public class LibraryFilterTests {
    private static readonly Guid AnimeFolder = Guid.NewGuid();
    private static readonly Guid MixedFolder = Guid.NewGuid();
    private static readonly Guid SkippedFolder = Guid.NewGuid();
    private static readonly Guid UnlistedFolder = Guid.NewGuid();

    private static readonly AnimeLibrary[] Admin = [
        new() { LibraryId = AnimeFolder, Mode = AnimeLibraryMode.Dedicated },
        new() { LibraryId = MixedFolder, Mode = AnimeLibraryMode.Mixed },
        new() { LibraryId = SkippedFolder, Mode = AnimeLibraryMode.Off }
    ];

    private static LibraryScope In(bool hasAnimeId, params Guid[] libraries) =>
        LibraryFilter.ScopeIn(Admin, libraries, () => hasAnimeId);

    [Test]
    public void ADedicatedLibrarySyncsEveryTitle() {
        Assert.That(In(false, AnimeFolder), Is.EqualTo(LibraryScope.Included));
        Assert.That(In(true, AnimeFolder), Is.EqualTo(LibraryScope.Included));
    }

    [Test]
    public void AMixedLibrarySyncsOnlyTitlesWithAnAnimeIdAndOnlyThroughIds() {
        Assert.That(In(true, MixedFolder), Is.EqualTo(LibraryScope.IdsOnly));
        Assert.That(In(false, MixedFolder), Is.EqualTo(LibraryScope.Excluded));
    }

    [Test]
    public void OffUnlistedAndUnknownLibrariesSyncNothing() {
        Assert.That(In(true, SkippedFolder), Is.EqualTo(LibraryScope.Excluded));
        Assert.That(In(true, UnlistedFolder), Is.EqualTo(LibraryScope.Excluded));
        Assert.That(In(true), Is.EqualTo(LibraryScope.Excluded));
    }

    [Test]
    public void AFolderInTwoLibrariesTakesTheMorePermissiveMode() {
        Assert.That(In(false, SkippedFolder, AnimeFolder), Is.EqualTo(LibraryScope.Included));
        Assert.That(In(true, SkippedFolder, MixedFolder), Is.EqualTo(LibraryScope.IdsOnly));
        Assert.That(In(true, MixedFolder, AnimeFolder), Is.EqualTo(LibraryScope.Included));
    }

    [Test]
    public void TheAnimeIdIsOnlyLookedAtForAMixedLibrary() {
        bool asked = false;
        LibraryFilter.ScopeIn(Admin, [AnimeFolder], () => asked = true);
        Assert.That(asked, Is.False);
    }

    [TestCase("AniList", "21", true)]
    [TestCase("AniDB", "4563", true)]
    [TestCase("anidb", "4563", true)]
    [TestCase("MyAnimeList", "20", true)]
    [TestCase("Kitsu", "11", true)]
    [TestCase("Tvdb", "79824", false)]
    [TestCase("Tmdb", "46260", false)]
    [TestCase("AniList", " ", false)]
    public void ASeriesAnimeIdCountsForItsEpisodes(string key, string value, bool expected) {
        var episode = new LibraryFixture().Episode("Fixture Show", 1, 1, new Dictionary<string, string> { [key] = value });
        Assert.That(LibraryFilter.HasAnimeId(episode), Is.EqualTo(expected));
    }

    [TestCase("Kitsu", true)]
    [TestCase("Tmdb", false)]
    public void AMovieCountsItsOwnIds(string key, bool expected) {
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { [key] = "1" });
        Assert.That(LibraryFilter.HasAnimeId(movie), Is.EqualTo(expected));
    }

    private string _animeListFile = null!;

    /// <summary>An anime list holding one anime movie (TMDB 12429) and one entry tied only to an IMDb ID.</summary>
    [OneTimeSetUp]
    public void WriteAnimeList() {
        _animeListFile = Path.Combine(Path.GetTempPath(), "anidoki-library-filter-" + Guid.NewGuid() + ".xml");
        File.WriteAllText(_animeListFile, """
            <anime-list>
              <anime anidbid="5975" tvdbid="movie" tmdbid="12429" />
              <anime anidbid="313" tvdbid="75534" defaulttvdbseason="0" imdbid="tt0114563" />
              <anime anidbid="23" tvdbid="76885" defaulttvdbseason="1" />
            </anime-list>
            """);
    }

    [OneTimeTearDown]
    public void DeleteAnimeList() => File.Delete(_animeListFile);

    private AnimeListIndex AnimeList() => new(() => _animeListFile);

    [TestCase("12429", true)]
    [TestCase("603", false)]
    public void AMovieCountsWhenTheAnimeListMapsItsTmdbId(string tmdbId, bool expected) {
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { ["Tmdb"] = tmdbId });
        Assert.That(LibraryFilter.HasAnimeId(movie, AnimeList()), Is.EqualTo(expected));
    }

    [Test]
    public void ASeriesTmdbIdIsNeverTakenForAnAnimeMovie() {
        var episode = new LibraryFixture().Episode("Fixture Show", 1, 1, new Dictionary<string, string> { ["Tmdb"] = "12429" });
        Assert.That(LibraryFilter.HasAnimeId(episode, AnimeList()), Is.False);
    }

    // Jellyfin's AniDB plugin finds movies by title, so a Western film can carry an anime's AniDB ID.
    [TestCase("5975", "12429", null, true, TestName = "The list ties the AniDB ID to the movie's TMDB ID")]
    [TestCase("313", null, "tt0114563", true, TestName = "The list ties the AniDB ID to the movie's IMDb ID")]
    [TestCase("313", "12429", null, true, TestName = "The movie's TMDB ID is another anime in the list")]
    [TestCase("313", "603", "tt2000000", false, TestName = "The AniDB ID belongs to another movie")]
    [TestCase("23", "603", "tt2000000", false, TestName = "Nothing in the list ties the AniDB ID to the movie")]
    [TestCase("14176", "38757", null, false, TestName = "The list has no row for the AniDB ID")]
    public void AMovieAniDbIdCountsOnlyWhenTheAnimeListTiesItToTheMovie(string aniDbId, string? tmdbId, string? imdbId, bool expected) {
        var ids = new Dictionary<string, string> { ["AniDB"] = aniDbId };
        if (tmdbId != null)
            ids["Tmdb"] = tmdbId;
        if (imdbId != null)
            ids["Imdb"] = imdbId;
        var movie = new LibraryFixture().Movie("Fixture Movie", ids);
        Assert.That(LibraryFilter.HasAnimeId(movie, AnimeList()), Is.EqualTo(expected));
    }

    [TestCase("Shoko Series", TestName = "Shoko matched the file, so its AniDB ID is trusted")]
    [TestCase("AniList", TestName = "Another anime ID still counts")]
    public void AMovieWithAnUncheckedAniDbIdStillCountsThroughATrustedId(string key) {
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { ["AniDB"] = "14176", [key] = "1", ["Tmdb"] = "38757" });
        Assert.That(LibraryFilter.HasAnimeId(movie, AnimeList()), Is.True);
    }

    [Test]
    public void AnEpisodeAniDbIdIsNotChecked() {
        var episode = new LibraryFixture().Episode("Fixture Show", 1, 1, new Dictionary<string, string> { ["AniDB"] = "14176" });
        Assert.That(LibraryFilter.HasAnimeId(episode, AnimeList()), Is.True);
    }

    private static Mock<ILibraryManager> Library() {
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = AnimeFolder.ToString("N"), Name = "Anime", Locations = ["/media/anime"] }]);
        return library;
    }

    [Test]
    public void WithNoAdminListEveryLibraryIsScanned() {
        // Strict: an empty list decides without looking up a library (D9; what a user who never picked libraries had).
        var library = new Mock<ILibraryManager>(MockBehavior.Strict).Object;

        Assert.That(LibraryFilter.Scope(null, library, AnimeList(), NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Included));
        Assert.That(LibraryFilter.Scope([], library, AnimeList(), NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Included));
    }

    [Test]
    public void OnceTheAdminChoosesItsListDecides() =>
        Assert.That(LibraryFilter.Scope(Admin, Library().Object, AnimeList(), NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Excluded));

    private static Mock<ILibraryManager> Folders() {
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Returns([
            new VirtualFolderInfo { ItemId = AnimeFolder.ToString("N"), Name = "Anime", Locations = ["/media/anime"] },
            new VirtualFolderInfo { ItemId = MixedFolder.ToString("N"), Name = "Mixed", Locations = ["/media/cartoons", "/media/more"] },
            new VirtualFolderInfo { ItemId = SkippedFolder.ToString("N"), Name = "Shows", Locations = ["/media/shows"] },
            new VirtualFolderInfo { ItemId = UnlistedFolder.ToString("N"), Name = "Films", Locations = ["/media/films"] }
        ]);
        return library;
    }

    private static List<string> Kept(IReadOnlyCollection<AnimeLibrary>? admin, ILibraryManager library, params string?[] folders) {
        var items = folders.Select(folder => (Item: new Series { Name = folder ?? "none" }, Folder: folder)).ToList();
        return LibraryFilter.WithoutOffLibraries(admin, library, NullLogger.Instance, items.Select(entry => entry.Item),
            series => items.First(entry => entry.Item == series).Folder).Select(series => series.Name).ToList();
    }

    [Test]
    public void OffAndUnlistedLibrariesAreDroppedBeforeAnythingIsRead() =>
        Assert.That(Kept(Admin, Folders().Object, "/media/anime", "/media/shows", "/media/more", "/media/films", null),
            Is.EqualTo(new[] { "/media/anime", "/media/more" }));

    [Test]
    public void WithNoAdminListNothingIsDropped() {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict).Object;

        Assert.That(Kept([], library, "/media/shows", null), Is.EqualTo(new[] { "/media/shows", "none" }));
    }

    [Test]
    public void UnreadableLibrariesDropNothing() {
        // Each item is still checked on its own afterwards, so keeping everything is slower but never wrong.
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Throws(new InvalidOperationException("fixture"));

        Assert.That(Kept(Admin, library.Object, "/media/shows"), Is.EqualTo(new[] { "/media/shows" }));
    }

    [Test]
    public void TheMatchListAsksTheSameQuestionAsSync() {
        // An item in no library, as Scope says for every user once the admin chooses: left out of the admin list too.
        Assert.That(LibraryFilter.InAnimeLibraries(Admin, Library().Object, AnimeList(), new Episode()), Is.False);
    }
}
