using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Execution;

/// <summary>How many times one sync reads each tracker entry, end to end through UpdateProviderStatus.</summary>
[TestFixture, NonParallelizable]
public class PipelineReadTests {
    private static readonly Dictionary<string, string> AniListId = new() { ["AniList"] = SyncHarness.EntryId.ToString() };

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    private static async Task<FakeTracker> Sync(ApiName provider, Func<SyncHarness, MediaBrowser.Controller.Entities.BaseItem> item,
        params Anime[] entries) {
        using var harness = new SyncHarness();
        var user = Guid.NewGuid();
        harness.ConfigureUser(user, false, false, provider);
        var tracker = harness.Tracker(provider);
        foreach (var entry in entries)
            tracker.Add(entry);
        tracker.SearchResults.Add(entries[0]);
        await harness.RunSingle(harness.NewUpdater(), item(harness), user, SyncUpdateSource.Playback, provider);
        return tracker;
    }

    [Test]
    public async Task IdPathReadsTheEntryOnce() {
        var tracker = await Sync(ApiName.AniList, harness => harness.Library.Episode("Fixture Show", 1, 5, AniListId),
            SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));

        Assert.That(tracker.Reads, Is.EqualTo(new[] { (500, false) }));
        Assert.That(tracker.Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task WalkReadsNothingAfterResolution() {
        var tracker = await Sync(ApiName.AniList, harness => harness.Library.Episode("Fixture Show", 1, 3, AniListId),
            SyncHarness.Entry(500, "Fixture Show", 1, Watching(0), SyncHarness.Related(RelationType.Sequel, 501)),
            SyncHarness.Entry(501, "Fixture Show Part 2", 12, Watching(0)));

        Assert.That(tracker.Reads, Is.EqualTo(new[] { (500, false), (500, true), (501, false) }));
        Assert.That(tracker.Writes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TitleMatchIsReadOnce() {
        var tracker = await Sync(ApiName.Mal, harness => harness.Library.Episode("Fixture Show", 1, 5),
            SyncHarness.Entry(600, "Fixture Show", 12, Watching(3)));

        Assert.That(tracker.Reads, Is.EqualTo(new[] { (600, false) }));
        Assert.That(tracker.Writes, Has.Count.EqualTo(1));
    }

    // Decision 9: resolution's cross-ID match carries no state, so the executor reads again; PR 8 can remove it.
    [Test]
    public async Task SimklStillReadsTwice() {
        var tracker = await Sync(ApiName.Simkl, harness => harness.Library.Episode("Fixture Show", 1, 5, AniListId),
            SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));

        Assert.That(tracker.CrossIdReads, Is.EqualTo(2));
        Assert.That(tracker.Writes, Has.Count.EqualTo(1));
    }
}
