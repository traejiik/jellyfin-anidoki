using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Trackers.TrackerTestSupport;

namespace jellyfin_anidoki_unit_tests.Trackers;

[TestFixture]
public class TrackerReadTests {
    private static IEnumerable<ApiName> Providers() => AllProviders;

    private static IEnumerable<ApiName> SearchingProviders() => AllProviders.Where(provider => provider != ApiName.Simkl);

    [TestCaseSource(nameof(Providers))]
    public async Task ReadEntryMapsAWatchingEntry(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3, RewatchCount = 1 }));

        var state = await adapter.ReadEntry(Ref(provider), CancellationToken.None);

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.Entry, Is.EqualTo(Ref(provider)));
        Assert.That(state.TotalEpisodes, Is.EqualTo(12));
        Assert.That(state.ReadAt, Is.EqualTo(Now));
        Assert.That(state.List, Is.EqualTo(new ListState(TrackerStatus.Watching, 3, false, adapter.Capabilities.ReportsRewatchCount ? 1 : null)));
        Assert.That(state.Titles?.Main, Is.EqualTo("Fixture Show"));
    }

    // Each client's conversion presents a rewatch in progress differently; all normalize to Completed + Rewatching.
    [TestCase(ApiName.Mal, Status.Completed)]
    [TestCase(ApiName.AniList, Status.Rewatching)]
    [TestCase(ApiName.Kitsu, Status.Watching)]
    [TestCase(ApiName.Shikimori, Status.Rewatching)]
    public async Task ARewatchInProgressIsCompletedAndRewatching(ApiName provider, Status providerStatus) {
        var (adapter, tracker) = Adapter(provider);
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, new MyListStatus { Status = providerStatus, IsRewatching = true, NumEpisodesWatched = 3, RewatchCount = 1 }));

        var state = await adapter.ReadEntry(Ref(provider), CancellationToken.None);

        Assert.That(state!.List!.Status, Is.EqualTo(TrackerStatus.Completed));
        Assert.That(state.List.Rewatching, Is.True);
    }

    [TestCaseSource(nameof(Providers))]
    public async Task AnEntryNotOnTheListHasNoListState(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        // Kitsu's client reports a missing library entry as an empty status with no rewatch count.
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, provider == ApiName.Kitsu ? new MyListStatus() : null));

        var state = await adapter.ReadEntry(Ref(provider), CancellationToken.None);

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.List, Is.Null);
    }

    [TestCaseSource(nameof(Providers))]
    public async Task AnUnreadableEntryReadsAsNull(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, null));
        tracker.FailingIds.Add(500);

        Assert.That(await adapter.ReadEntry(Ref(provider), CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task SimklCannotReadWithoutItsCrossReference() {
        var (adapter, tracker) = Adapter(ApiName.Simkl);
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, null));

        Assert.That(await adapter.ReadEntry(new EntryRef(ApiName.Simkl, 500, null), CancellationToken.None), Is.Null);
    }

    [TestCaseSource(nameof(SearchingProviders))]
    public async Task SearchReturnsCandidatesWithTheirTitles(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        var entry = SyncHarness.Entry(500, "Fixture Show", 12, null);
        entry.AlternativeTitles.En = "Fixture Show English";
        entry.AlternativeTitles.Synonyms.Add("Fixture");
        tracker.Add(entry);
        tracker.SearchResults.Add(entry);

        var candidates = await adapter.Search("Fixture Show", CancellationToken.None);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].Entry, Is.EqualTo(Ref(provider) with { Lookup = null }));
        Assert.That(candidates[0].TotalEpisodes, Is.EqualTo(12));
        Assert.That(candidates[0].Titles.Main, Is.EqualTo("Fixture Show"));
        Assert.That(candidates[0].Titles.English, Is.EqualTo("Fixture Show English"));
        Assert.That(candidates[0].Titles.Synonyms, Is.EqualTo(new[] { "Fixture" }));
    }

    [Test]
    public async Task SimklCannotSearch() {
        var (adapter, tracker) = Adapter(ApiName.Simkl);
        var entry = tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, null));
        tracker.SearchResults.Add(entry);

        Assert.That(await adapter.Search("Fixture Show", CancellationToken.None), Is.Empty);
    }

    [TestCaseSource(nameof(SearchingProviders))]
    public async Task RelationsAreMappedAndAFailedReadIsNull(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        tracker.Add(SyncHarness.Entry(600, "Fixture Show", 12, null,
            SyncHarness.Related(RelationType.Sequel, 601), SyncHarness.Related(RelationType.Side_Story, 602)));

        var relations = await adapter.GetRelations(Ref(provider, 600), CancellationToken.None);

        Assert.That(relations, Is.EqualTo(new[] {
            new EntryRelation(RelationKind.Sequel, Ref(provider, 601)),
            new EntryRelation(RelationKind.SideStory, Ref(provider, 602))
        }));
        tracker.FailingIds.Add(600);
        Assert.That(await adapter.GetRelations(Ref(provider, 600), CancellationToken.None), Is.Null);
    }

    [TestCase(ApiName.Mal, 2)]
    [TestCase(ApiName.AniList, 1)]
    [TestCase(ApiName.Kitsu, 3)]
    public async Task DirectProvidersUseTheirOwnIdWithoutACall(ApiName provider, int expectedId) {
        var (adapter, _) = Adapter(provider);

        var entry = await adapter.FindByCrossIds(new ResolvedIds(1, 2, 3, 4), "Fixture Show", MediaKind.Series, CancellationToken.None);

        Assert.That(entry, Is.EqualTo(new EntryRef(provider, expectedId, null)));
        Assert.That(await adapter.FindByCrossIds(new ResolvedIds(null, null, null, 4), "Fixture Show", MediaKind.Series, CancellationToken.None), Is.Null);
    }

    [TestCase(ApiName.Annict)]
    [TestCase(ApiName.Shikimori)]
    public async Task AnnictAndShikimoriMatchSearchResultsOnTheMalId(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        tracker.SearchResults.Add(tracker.Add(SyncHarness.Entry(7, "Other", 12, null)));
        tracker.SearchResults.Add(tracker.Add(SyncHarness.Entry(2, "Fixture Show", 12, null)));

        var entry = await adapter.FindByCrossIds(new ResolvedIds(1, 2, 3, 4), "Fixture Show", MediaKind.Series, CancellationToken.None);

        Assert.That(entry, Is.EqualTo(new EntryRef(provider, 2, "alt-2")));
        Assert.That(await adapter.FindByCrossIds(new ResolvedIds(1, null, 3, 4), "Fixture Show", MediaKind.Series, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task SimklResolvesThroughCrossIdsAndKeepsThemForLaterCalls() {
        var (adapter, tracker) = Adapter(ApiName.Simkl);
        tracker.Add(SyncHarness.Entry(2, "Fixture Show", 12, null));
        var ids = new ResolvedIds(1, 2, 3, 4);

        var entry = await adapter.FindByCrossIds(ids, "Fixture Show", MediaKind.Movie, CancellationToken.None);

        Assert.That(entry, Is.EqualTo(new EntryRef(ApiName.Simkl, 2, null, new CrossReference(ids, "Fixture Show", MediaKind.Movie))));
        Assert.That(await adapter.ReadEntry(entry!, CancellationToken.None), Is.Not.Null);
        Assert.That(await adapter.FindByCrossIds(new ResolvedIds(null, null, null, null), "Fixture Show", MediaKind.Series, CancellationToken.None), Is.Null);
    }

    [Test]
    public void ReadsStopWhenCancelled() {
        var (adapter, _) = Adapter(ApiName.AniList);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => adapter.ReadEntry(Ref(ApiName.AniList), cancellation.Token));
        Assert.ThrowsAsync<OperationCanceledException>(() => adapter.Search("x", cancellation.Token));
    }
}
