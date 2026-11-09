using System;
using System.Collections.Generic;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models;
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

    [TestCase("12429", true)]
    [TestCase("603", false)]
    public void AMovieCountsWhenTheAnimeListMapsItsTmdbId(string tmdbId, bool expected) {
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { ["Tmdb"] = tmdbId });
        Assert.That(LibraryFilter.HasAnimeId(movie, id => id == "12429"), Is.EqualTo(expected));
    }

    [Test]
    public void ASeriesTmdbIdIsNeverTakenForAnAnimeMovie() {
        var episode = new LibraryFixture().Episode("Fixture Show", 1, 1, new Dictionary<string, string> { ["Tmdb"] = "12429" });
        Assert.That(LibraryFilter.HasAnimeId(episode, _ => true), Is.False);
    }

    private static Mock<ILibraryManager> Library() {
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = AnimeFolder.ToString("N"), Name = "Anime", Locations = ["/media/anime"] }]);
        return library;
    }

    [Test]
    public void WithNoAdminListEachUsersOwnChoiceStillApplies() {
        var library = Library().Object;
        var all = new UserConfig { LibraryToCheck = [] };
        var some = new UserConfig { LibraryToCheck = [AnimeFolder.ToString("N")] };

        Assert.That(LibraryFilter.Scope(null, all, library, NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Included));
        Assert.That(LibraryFilter.Scope([], all, library, NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Included));
        // An item outside the user's picked libraries, as ManualMarkTrackingTests checks through the old entry point.
        Assert.That(LibraryFilter.Scope([], some, library, NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Excluded));
    }

    [Test]
    public void OnceTheAdminChoosesAUsersAllLibrariesNoLongerApplies() {
        var all = new UserConfig { LibraryToCheck = [] };

        Assert.That(LibraryFilter.Scope(Admin, all, Library().Object, NullLogger.Instance, new Episode()), Is.EqualTo(LibraryScope.Excluded));
    }
}
