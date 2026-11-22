using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>D9: the first start of 2.0.0 fills the admin's anime libraries from the users' earlier choices, once.</summary>
[TestFixture]
public class AnimeLibrarySeedingTests {
    private static readonly Guid Shows = Guid.NewGuid(), Movies = Guid.NewGuid(), Mixed = Guid.NewGuid(), Music = Guid.NewGuid();
    private static readonly SeedLibrary[] Libraries = [new(Shows, "tvshows"), new(Movies, "movies"), new(Mixed, null), new(Music, "music")];
    private static readonly Guid Test = Guid.NewGuid(), Family = Guid.NewGuid(), Guest = Guid.NewGuid();

    private static string? Name(Guid id) => id == Test ? "test" : id == Family ? "family" : id == Guest ? "guest" : null;

    private static UserConfig User(Guid id, string[]? libraries, bool linked = true) => new() {
        UserId = id,
        LibraryToCheck = libraries,
        UserApiAuth = linked ? [new UserApiAuth { Name = ApiName.AniList, AccessToken = "token" }] : []
    };

    private static string Id(Guid id) => id.ToString("N");

    [Test]
    public void EveryLibraryALinkedUserChoseBecomesDedicatedAndNamesWhoChoseIt() {
        var configuration = new PluginConfiguration { UserConfig = [User(Test, [Id(Shows)]), User(Family, [Id(Shows), Id(Movies)])] };

        var result = AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(result, Is.EqualTo(new SeedResult(true, 2, 2)));
        Assert.That(configuration.AnimeLibraries.Select(entry => (entry.LibraryId, entry.Mode)),
            Is.EqualTo(new[] { (Shows, AnimeLibraryMode.Dedicated), (Movies, AnimeLibraryMode.Dedicated) }));
        Assert.That(configuration.AnimeLibrarySeeds.Select(seed => (seed.LibraryId, string.Join(",", seed.ChosenBy))),
            Is.EqualTo(new[] { (Shows, "test,family"), (Movies, "family") }));
        Assert.That(configuration.AnimeLibrariesSeeded, Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AUserWhoHadEveryLibrarySeedsEveryShowMovieAndMixedLibrary(bool missing) {
        var configuration = new PluginConfiguration { UserConfig = [User(Test, missing ? null : [])] };

        AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(configuration.AnimeLibraries.Select(entry => entry.LibraryId), Is.EqualTo(new[] { Shows, Movies, Mixed }), "music is never seeded");
    }

    [Test]
    public void UsersWithoutALinkedTrackerAndDeletedUsersDoNotCount() {
        var configuration = new PluginConfiguration {
            UserConfig = [User(Test, [Id(Shows)]), User(Guest, null, linked: false), User(Guid.NewGuid(), [Id(Movies)])]
        };

        var result = AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(configuration.AnimeLibraries.Select(entry => entry.LibraryId), Is.EqualTo(new[] { Shows }));
        Assert.That(result.Users, Is.EqualTo(1));
    }

    [Test]
    public void AnAdminListAlreadySetIsLeftAlone() {
        var chosen = new AnimeLibrary { LibraryId = Mixed, Mode = AnimeLibraryMode.Mixed };
        var configuration = new PluginConfiguration { AnimeLibraries = [chosen], UserConfig = [User(Test, [Id(Shows)])] };

        var result = AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(result, Is.EqualTo(new SeedResult(true, 0, 0)));
        Assert.That(configuration.AnimeLibraries, Is.EqualTo(new[] { chosen }));
        Assert.That(configuration.AnimeLibrarySeeds, Is.Empty, "no note to show");
        Assert.That(configuration.AnimeLibrariesSeeded, Is.True);
    }

    [Test]
    public void WithNobodyLinkedTheListStaysEmptyAndNothingIsNoted() {
        var configuration = new PluginConfiguration { UserConfig = [User(Guest, [Id(Shows)], linked: false)] };

        var result = AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(result.Libraries, Is.Zero);
        Assert.That(configuration.AnimeLibraries, Is.Empty);
        Assert.That(configuration.AnimeLibrariesSeeded, Is.True);
    }

    [Test]
    public void WithNoLibrariesListedYetItWaitsForTheNextStart() {
        var configuration = new PluginConfiguration { UserConfig = [User(Test, [Id(Shows)])] };

        var result = AnimeLibrarySeeding.Seed(configuration, [], Name);

        Assert.That(result.Changed, Is.False);
        Assert.That(configuration.AnimeLibrariesSeeded, Is.False);
        Assert.That(configuration.UserConfig![0].LibraryToCheck, Is.Not.Null, "the old choice is kept for that start");
    }

    [Test]
    public void TheOldChoicesAreDroppedAndItRunsOnce() {
        var configuration = new PluginConfiguration { UserConfig = [User(Test, [Id(Shows)])] };
        AnimeLibrarySeeding.Seed(configuration, Libraries, Name);
        configuration.AnimeLibraries = [];

        var again = AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(configuration.UserConfig![0].LibraryToCheck, Is.Null);
        Assert.That(again.Changed, Is.False);
        Assert.That(configuration.AnimeLibraries, Is.Empty, "an admin who clears the list later is not overruled");
    }

    [Test]
    public void ALibraryIdIsMatchedWithOrWithoutDashes() {
        var configuration = new PluginConfiguration { UserConfig = [User(Test, [Shows.ToString("D")])] };

        AnimeLibrarySeeding.Seed(configuration, Libraries, Name);

        Assert.That(configuration.AnimeLibraries.Single().LibraryId, Is.EqualTo(Shows));
    }
}
