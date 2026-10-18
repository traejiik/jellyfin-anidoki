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
public class TrackerApplyTests {
    private static IEnumerable<ApiName> Providers() => AllProviders;

    // The stored entry has a server-side rewatch count of 1 for every provider; MAL's reads hide it.
    private static FakeTracker Seed(FakeTracker tracker, Status status, int progress, bool rewatching) {
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, new MyListStatus { Status = status, NumEpisodesWatched = progress, IsRewatching = rewatching, RewatchCount = 1 }));
        return tracker;
    }

    private static async Task<EntryState> Read(ITrackerProvider adapter, ApiName provider) =>
        (await adapter.ReadEntry(Ref(provider), CancellationToken.None))!;

    private static string[] Writes(FakeTracker tracker) => tracker.Writes.Select(write => write.ToString()).ToArray();

    [TestCaseSource(nameof(Providers))]
    public async Task APlainChangeIsOneWrite(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, Status.Watching, 3, false);

        var receipt = await adapter.Apply(await Read(adapter, provider), new EntryChange(5, TrackerStatus.Watching), CancellationToken.None);

        Assert.That(Writes(tracker), Is.EqualTo(new[] { "write(id=500, progress=5, status=Watching, rewatching=-, count=-, start=False, end=False)" }));
        Assert.That(receipt, Is.EqualTo(new WriteReceipt(adapter.Capabilities.TracksProgress ? 5 : null, TrackerStatus.Watching, null, 1, true)));
    }

    [TestCaseSource(nameof(Providers))]
    public async Task DatesAreSentWithTheChange(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, Status.Watching, 11, false);
        var finished = new DateTime(2026, 10, 17);

        await adapter.Apply(await Read(adapter, provider), new EntryChange(12, TrackerStatus.Completed, EndDate: finished), CancellationToken.None);

        Assert.That(tracker.Writes.Single().EndDate, Is.True);
    }

    [TestCase(ApiName.Mal, new[] { "write(id=500, progress=5, status=Completed, rewatching=True, count=-, start=False, end=False)" })]
    [TestCase(ApiName.AniList, new[] { "write(id=500, progress=5, status=Completed, rewatching=True, count=-, start=False, end=False)", "write(id=500, progress=5, status=Completed, rewatching=True, count=-, start=False, end=False)" })]
    [TestCase(ApiName.Kitsu, new[] { "write(id=500, progress=5, status=Rewatching, rewatching=True, count=-, start=False, end=False)" })]
    [TestCase(ApiName.Shikimori, new[] { "write(id=500, progress=5, status=Rewatching, rewatching=-, count=-, start=False, end=False)" })]
    public async Task StartingARewatch(ApiName provider, string[] expected) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, Status.Completed, 12, false);

        var receipt = await adapter.Apply(await Read(adapter, provider), new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Start), CancellationToken.None);

        Assert.That(Writes(tracker), Is.EqualTo(expected));
        Assert.That(receipt!.Complete, Is.True);
        Assert.That(receipt.Status, Is.EqualTo(TrackerStatus.Completed));
    }

    [TestCase(ApiName.Mal, Status.Completed, "write(id=500, progress=5, status=Completed, rewatching=True, count=-, start=False, end=False)")]
    [TestCase(ApiName.AniList, Status.Rewatching, "write(id=500, progress=5, status=Rewatching, rewatching=-, count=-, start=False, end=False)")]
    [TestCase(ApiName.Kitsu, Status.Watching, "write(id=500, progress=5, status=Watching, rewatching=-, count=-, start=False, end=False)")]
    [TestCase(ApiName.Shikimori, Status.Rewatching, "write(id=500, progress=5, status=Rewatching, rewatching=-, count=-, start=False, end=False)")]
    public async Task ContinuingARewatch(ApiName provider, Status providerStatus, string expected) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, providerStatus, 3, true);

        var receipt = await adapter.Apply(await Read(adapter, provider), new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Continue), CancellationToken.None);

        Assert.That(Writes(tracker), Is.EqualTo(new[] { expected }));
        Assert.That(receipt!.Progress, Is.EqualTo(5));
    }

    [TestCase(ApiName.Mal, Status.Completed, new[] { "write(id=500, progress=12, status=Completed, rewatching=False, count=-, start=False, end=False)", "write(id=500, progress=12, status=Completed, rewatching=False, count=2, start=False, end=False)" })]
    [TestCase(ApiName.AniList, Status.Rewatching, new[] { "write(id=500, progress=12, status=Completed, rewatching=False, count=2, start=False, end=False)" })]
    [TestCase(ApiName.Kitsu, Status.Watching, new[] { "write(id=500, progress=12, status=Completed, rewatching=False, count=2, start=False, end=False)" })]
    [TestCase(ApiName.Shikimori, Status.Rewatching, new[] { "write(id=500, progress=12, status=Completed, rewatching=False, count=2, start=False, end=False)" })]
    public async Task FinishingARewatchCountsItOnce(ApiName provider, Status providerStatus, string[] expected) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, providerStatus, 11, true);

        var receipt = await adapter.Apply(await Read(adapter, provider), new EntryChange(12, TrackerStatus.Completed, RewatchTransition.Finish), CancellationToken.None);

        Assert.That(Writes(tracker), Is.EqualTo(expected));
        Assert.That(receipt, Is.EqualTo(new WriteReceipt(12, TrackerStatus.Completed, false, 2, true)));
    }

    [Test]
    public async Task MalReportsHowFarAFinishGotWhenTheCountWriteFails() {
        var (adapter, tracker) = Adapter(ApiName.Mal);
        Seed(tracker, Status.Completed, 11, true);
        tracker.FailingWrites.Add(2);

        var receipt = await adapter.Apply(await Read(adapter, ApiName.Mal), new EntryChange(12, TrackerStatus.Completed, RewatchTransition.Finish), CancellationToken.None);

        Assert.That(tracker.Writes, Has.Count.EqualTo(2));
        Assert.That(receipt, Is.EqualTo(new WriteReceipt(12, TrackerStatus.Completed, false, 1, false)));
    }

    [Test]
    public async Task MalSendsNoCountWhenTheFirstFinishWriteFails() {
        var (adapter, tracker) = Adapter(ApiName.Mal);
        Seed(tracker, Status.Completed, 11, true);
        tracker.FailingWrites.Add(1);

        var receipt = await adapter.Apply(await Read(adapter, ApiName.Mal), new EntryChange(12, TrackerStatus.Completed, RewatchTransition.Finish), CancellationToken.None);

        Assert.That(tracker.Writes, Has.Count.EqualTo(1));
        Assert.That(receipt, Is.Null);
    }

    [Test]
    public async Task AniListReportsHowFarAStartGotWhenTheSecondCallFails() {
        var (adapter, tracker) = Adapter(ApiName.AniList);
        Seed(tracker, Status.Completed, 12, false);
        tracker.FailingWrites.Add(2);

        var receipt = await adapter.Apply(await Read(adapter, ApiName.AniList), new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Start), CancellationToken.None);

        Assert.That(receipt!.Complete, Is.False);
        Assert.That(receipt.Progress, Is.EqualTo(5));
    }

    [TestCase(ApiName.Annict)]
    [TestCase(ApiName.Simkl)]
    public async Task ARewatchOnAProviderWithoutRewatchIsRefused(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, Status.Completed, 12, false);
        var state = await Read(adapter, provider);

        Assert.ThrowsAsync<ArgumentException>(() => adapter.Apply(state, new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Start), CancellationToken.None));
        Assert.That(tracker.Writes, Is.Empty);
    }

    [Test]
    public async Task ARewatchMustTargetCompleted() {
        var (adapter, tracker) = Adapter(ApiName.AniList);
        Seed(tracker, Status.Completed, 12, false);
        var state = await Read(adapter, ApiName.AniList);

        Assert.ThrowsAsync<ArgumentException>(() => adapter.Apply(state, new EntryChange(5, TrackerStatus.Watching, RewatchTransition.Start), CancellationToken.None));
        Assert.That(tracker.Writes, Is.Empty);
    }

    [Test]
    public async Task SimklWritesWithItsCrossIdsAndMediaKind() {
        var (adapter, tracker) = Adapter(ApiName.Simkl);
        Seed(tracker, Status.Watching, 3, false);

        await adapter.Apply(await Read(adapter, ApiName.Simkl), new EntryChange(5, TrackerStatus.Watching), CancellationToken.None);

        Assert.That(tracker.WriteContexts.Single(), Is.EqualTo(new WriteContext(null!, 500, true)));
    }

    [TestCase(ApiName.Annict)]
    [TestCase(ApiName.Shikimori)]
    public async Task AlternativeIdProvidersWriteByTheirAlternativeId(ApiName provider) {
        var (adapter, tracker) = Adapter(provider);
        Seed(tracker, Status.Watching, 3, false);

        await adapter.Apply(await Read(adapter, provider), new EntryChange(5, TrackerStatus.Watching), CancellationToken.None);

        Assert.That(tracker.WriteContexts.Single().AlternativeId, Is.EqualTo("alt-500"));
    }

    [Test]
    public async Task AReceiptInRewatchingStatusIsCompletedAndRewatching() {
        var (adapter, tracker) = Adapter(ApiName.AniList);
        Seed(tracker, Status.Rewatching, 3, true);

        var receipt = await adapter.Apply(await Read(adapter, ApiName.AniList), new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Continue), CancellationToken.None);

        Assert.That(receipt!.Status, Is.EqualTo(TrackerStatus.Completed));
        Assert.That(receipt.Rewatching, Is.True);
    }

    [Test]
    public async Task WritesStopWhenCancelled() {
        var (adapter, tracker) = Adapter(ApiName.AniList);
        Seed(tracker, Status.Watching, 3, false);
        var state = await Read(adapter, ApiName.AniList);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => adapter.Apply(state, new EntryChange(5, TrackerStatus.Watching), cancellation.Token));
        Assert.That(tracker.Writes, Is.Empty);
    }
}
