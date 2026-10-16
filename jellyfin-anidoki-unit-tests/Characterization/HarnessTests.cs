using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

[TestFixture, NonParallelizable]
public class HarnessTests {
    [Test]
    public void EpisodeResolvesItsSeasonAndSeries() {
        var library = new LibraryFixture();

        var episode = library.Episode("Fixture Show", 2, 5, new Dictionary<string, string> { ["AniList"] = "500" });

        Assert.That(episode.Season?.IndexNumber, Is.EqualTo(2));
        Assert.That(episode.Series?.ProviderIds["AniList"], Is.EqualTo("500"));
        Assert.That(episode.SeriesName, Is.EqualTo("Fixture Show"));
        Assert.That(episode.IndexNumber, Is.EqualTo(5));
    }

    [Test]
    public async Task WatchingEntryAdvancesToThePlayedEpisode() {
        using var harness = new SyncHarness();
        var user = Guid.NewGuid();
        harness.ConfigureUser(user, planToWatchOnly: false, rewatchCompleted: false, ApiName.AniList);
        harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12,
            new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 }));
        var episode = harness.Library.Episode("Fixture Show", 1, 5,
            new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });

        string result = await harness.RunSingle(harness.NewUpdater(), episode, user, SyncUpdateSource.Playback, ApiName.AniList);

        Assert.That(result, Is.EqualTo(
            "write(id=500, progress=5, status=Watching, rewatching=-, count=-, start=False, end=False) | Confirmed:progress@500"));
    }
}
