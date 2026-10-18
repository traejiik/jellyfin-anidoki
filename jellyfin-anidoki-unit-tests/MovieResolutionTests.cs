using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities.Movies;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests;

/// <summary>
/// A movie whose IDs resolve must be written to that entry, not to whatever the title search finds first,
/// and syncing it must not change the library item.
/// </summary>
[TestFixture, NonParallelizable]
public class MovieResolutionTests {
    private const int TitleMatchId = 901;

    private readonly Guid _user = Guid.NewGuid();
    private SyncHarness _harness = null!;

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        // Title search returns 901; ARM resolves every ID to SyncHarness.EntryId (500), a same-named entry.
        _harness.SeedAll(SyncHarness.Entry(TitleMatchId, "Fixture Movie", 1, null), SyncHarness.Entry(SyncHarness.EntryId, "Fixture Movie", 1, null));
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private Movie MovieWithAniListId() => _harness.Library.Movie("Fixture Movie", new Dictionary<string, string> { ["AniList"] = "900" });

    private async Task Sync(Movie movie, ApiName provider) {
        _harness.ConfigureUser(_user, false, false, provider);
        await _harness.Run(_harness.NewUpdater(), movie, _user, SyncUpdateSource.Playback, provider);
    }

    [TestCase(ApiName.Mal)]
    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Kitsu)]
    public async Task AMovieWithResolvedIdsIsWrittenToTheResolvedEntry(ApiName provider) {
        await Sync(MovieWithAniListId(), provider);

        var write = _harness.Tracker(provider).Writes.Single();
        Assert.That(write.EntryId, Is.EqualTo(SyncHarness.EntryId));
        Assert.That(write.Progress, Is.EqualTo(1));
    }

    [Test]
    public async Task AMovieWithoutIdsStillFallsBackToTitleSearch() {
        await Sync(_harness.Library.Movie("Fixture Movie"), ApiName.Mal);

        Assert.That(_harness.Tracker(ApiName.Mal).Writes.Single().EntryId, Is.EqualTo(TitleMatchId));
    }

    /// <summary>Simkl's movie write has not been checked against a real account, so movies stay off its ID path.</summary>
    [Test]
    public async Task SimklStillReceivesNoMovies() {
        await Sync(MovieWithAniListId(), ApiName.Simkl);

        Assert.That(_harness.Tracker(ApiName.Simkl).Writes, Is.Empty);
    }

    [Test]
    public async Task SyncingAMovieLeavesItsIndexNumberAlone() {
        var movie = MovieWithAniListId();

        await Sync(movie, ApiName.Mal);

        Assert.That(movie.IndexNumber, Is.Null);
    }
}
