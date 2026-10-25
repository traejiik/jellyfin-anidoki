using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Trackers.TrackerTestSupport;

namespace jellyfin_anidoki_unit_tests.Execution;

/// <summary>Passes reads to the wrapped tracker; <see cref="OnApply"/> replaces writes when set.</summary>
internal sealed class SpyTracker(ITrackerProvider inner) : ITrackerProvider {
    public Func<EntryState, EntryChange, Task<WriteReceipt?>>? OnApply { get; set; }

    public List<EntryChange> Changes { get; } = new();

    public ApiName Name => inner.Name;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) => inner.ReadEntry(entry, cancellationToken);

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => inner.Search(title, cancellationToken);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) =>
        inner.GetRelations(entry, cancellationToken);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
        inner.FindByCrossIds(ids, title, kind, cancellationToken);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) {
        Changes.Add(change);
        return OnApply != null ? OnApply(current, change) : inner.Apply(current, change, cancellationToken);
    }
}

[TestFixture]
public class ExecutorTests {
    private static readonly PlanRules Off = new(false, false);
    private static readonly PlanRules RewatchOn = new(false, true);

    private static MyListStatus ListStatus(Status status, int progress, bool rewatching = false, int? count = 0) =>
        new() { Status = status, NumEpisodesWatched = progress, IsRewatching = rewatching, RewatchCount = count };

    private static (SpyTracker Tracker, FakeTracker Fake) Seed(ApiName provider, params Anime[] entries) {
        var (adapter, fake) = Adapter(provider);
        foreach (var entry in entries)
            fake.Add(entry);
        return (new SpyTracker(adapter), fake);
    }

    private static Anime Entry(int id, int total, MyListStatus? status) => SyncHarness.Entry(id, "Fixture Show", total, status);

    private static ResolvedTarget Target(ApiName provider, int episode, int id = 500, EntryState? state = null,
        TargetRole role = TargetRole.Played) =>
        new(Ref(provider, id), episode, role, "Fixture Show", state);

    private static Resolved Played(ApiName provider, int episode, params ResolvedTarget[] earlierParts) =>
        new(Target(provider, episode), ResolvedBy.Ids, earlierParts);

    private static async Task<PlaybackOutcomeCollector> Run(ITrackerProvider tracker, SourceResolution resolution, PlanRules? rules = null,
        PlanPolicy policy = PlanPolicy.Automatic, CancellationToken cancellationToken = default) {
        var collector = new PlaybackOutcomeCollector();
        await new Executor(tracker, rules ?? Off, policy, collector, new FixedTime(Now), NullLogger.Instance).Execute(resolution, cancellationToken);
        return collector;
    }

    private static string[] Summary(PlaybackOutcomeCollector collector) =>
        collector.Outcomes.Select(outcome => $"{outcome.Kind}:{outcome.Step}@{outcome.TargetId}").ToArray();

    private static string[] Writes(FakeTracker fake) => fake.Writes.Select(write => write.ToString()).ToArray();

    [Test]
    public async Task ConfirmedWriteRecordsTheReceipt() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));

        var collector = await Run(tracker, Played(ApiName.AniList, 5));

        Assert.That(fake.Writes, Has.Count.EqualTo(1));
        Assert.That(collector.Outcomes.Single(), Is.EqualTo(new ProviderWriteOutcome(ApiName.AniList, "500", "Fixture Show",
            OutcomeKind.Confirmed, 5, Status.Watching, false, "progress", true)));
    }

    [Test]
    public async Task NullReceiptIsUnconfirmed() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        fake.FailingWrites.Add(1);

        var collector = await Run(tracker, Played(ApiName.AniList, 5));

        Assert.That(Summary(collector), Is.EqualTo(new[] { "Unconfirmed:progress@500" }));
        Assert.That(collector.Outcomes.Single().ConfirmedProgress, Is.Null);
    }

    [Test]
    public async Task PartialAniListStartRecordsBothParts() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Completed, 12, count: 1)));
        fake.FailingWrites.Add(2);

        var collector = await Run(tracker, Played(ApiName.AniList, 1), RewatchOn);

        Assert.That(Summary(collector), Is.EqualTo(new[] { "Confirmed:progress@500", "Unconfirmed:progress@500" }));
        Assert.That(collector.HasConfirmedChange, Is.True);
    }

    [Test]
    public async Task PartialMalFinishRecordsBothParts() {
        var (tracker, fake) = Seed(ApiName.Mal, Entry(500, 12, ListStatus(Status.Completed, 11, true)));
        fake.FailingWrites.Add(2);

        var collector = await Run(tracker, Played(ApiName.Mal, 12), RewatchOn);

        Assert.That(Summary(collector), Is.EqualTo(new[] { "Confirmed:progress@500", "Unconfirmed:rewatch-count@500" }));
    }

    [Test]
    public async Task MultiCallSuccessIsOneOutcome() {
        var (aniList, aniListFake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Completed, 12, count: 1)));
        var start = await Run(aniList, Played(ApiName.AniList, 1), RewatchOn);

        var (mal, malFake) = Seed(ApiName.Mal, Entry(500, 12, ListStatus(Status.Completed, 11, true)));
        var finish = await Run(mal, Played(ApiName.Mal, 12), RewatchOn);

        Assert.That((aniListFake.Writes.Count, malFake.Writes.Count), Is.EqualTo((2, 2)));
        Assert.That(Summary(start), Is.EqualTo(new[] { "Confirmed:progress@500" }));
        Assert.That(Summary(finish), Is.EqualTo(new[] { "Confirmed:rewatch-count@500" }));
    }

    [Test]
    public async Task UnmeaningfulWriteIsNoChange() {
        var (tracker, fake) = Seed(ApiName.Mal, Entry(500, 12, ListStatus(Status.Completed, 5, true)));

        var collector = await Run(tracker, Played(ApiName.Mal, 5), RewatchOn);

        Assert.That(tracker.Changes.Single().Rewatch, Is.EqualTo(RewatchTransition.Continue));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
        Assert.That(Summary(collector), Is.EqualTo(new[] { "NoChange:progress@500" }));
        Assert.That(collector.Outcomes.Single().MeaningfulChange, Is.False);
    }

    [Test]
    public void ApplyThatThrowsRecordsUnconfirmedAndRethrows() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new InvalidOperationException("boom");
        var collector = new PlaybackOutcomeCollector();
        var executor = new Executor(tracker, Off, PlanPolicy.Automatic, collector, new FixedTime(Now), NullLogger.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(() => executor.Execute(Played(ApiName.AniList, 5), CancellationToken.None));
        Assert.That(Summary(collector), Is.EqualTo(new[] { "Unconfirmed:progress@500" }));
    }

    [Test]
    public void CancelledTokenStopsBeforeTheNextTarget() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var collector = new PlaybackOutcomeCollector();
        var executor = new Executor(tracker, Off, PlanPolicy.Automatic, collector, new FixedTime(Now), NullLogger.Instance);
        var resolution = new Resolved(Target(ApiName.AniList, 3, 501), ResolvedBy.SequelChain, [Target(ApiName.AniList, 12, role: TargetRole.EarlierPart)]);

        Assert.That(async () => await executor.Execute(resolution, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That((fake.Reads.Count, fake.Writes.Count, collector.Outcomes.Length), Is.EqualTo((0, 0, 0)));
    }

    [Test]
    public void CancelledInsideApplyIsUnconfirmed() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new OperationCanceledException();
        var collector = new PlaybackOutcomeCollector();
        var executor = new Executor(tracker, Off, PlanPolicy.Automatic, collector, new FixedTime(Now), NullLogger.Instance);

        Assert.That(async () => await executor.Execute(Played(ApiName.AniList, 5), CancellationToken.None), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Summary(collector), Is.EqualTo(new[] { "Unconfirmed:progress@500" }));
    }

    [Test]
    public async Task DecisionsRecordTheDisplayTitle() {
        var entry = Entry(500, 12, ListStatus(Status.Completed, 12, count: 1));
        entry.Title = " ";
        entry.AlternativeTitles.Synonyms = ["Synonym Title"];
        var (tracker, fake) = Seed(ApiName.AniList, entry);

        var collector = await Run(tracker, Played(ApiName.AniList, 5));

        Assert.That(fake.Writes, Is.Empty);
        Assert.That(Summary(collector), Is.EqualTo(new[] { "Skipped:rewatch-disabled@500" }));
        Assert.That(collector.Outcomes.Single().TargetTitle, Is.EqualTo("Synonym Title"));
    }

    [Test]
    public async Task StatusOnlyOutcomeHasNoProgress() {
        var (tracker, fake) = Seed(ApiName.Annict, Entry(500, 12, null));

        var collector = await Run(tracker, Played(ApiName.Annict, 12));

        Assert.That(Writes(fake), Is.EqualTo(new[] { "write(id=500, progress=1, status=Completed, rewatching=-, count=-, start=False, end=False)" }));
        var outcome = collector.Outcomes.Single();
        Assert.That((outcome.Kind, outcome.StatusOnly, outcome.ConfirmedProgress, outcome.Step, outcome.TargetId),
            Is.EqualTo((OutcomeKind.Confirmed, true, (int?)null, "status", "alt-500")));
    }

    [Test]
    public async Task RewatchReceiptReportsRewatching() {
        var (aniList, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Rewatching, 3, true, 1)));
        var aniListContinue = await Run(aniList, Played(ApiName.AniList, 5));

        var (mal, _) = Seed(ApiName.Mal, Entry(500, 12, ListStatus(Status.Completed, 3, true)));
        var malContinue = await Run(mal, Played(ApiName.Mal, 5), RewatchOn);

        var (finishing, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Rewatching, 11, true, 1)));
        var finish = await Run(finishing, Played(ApiName.AniList, 12));

        Assert.That(new[] { aniListContinue, malContinue, finish }.Select(collector => collector.Outcomes.Single().ConfirmedStatus),
            Is.EqualTo(new Status?[] { Status.Rewatching, Status.Rewatching, Status.Completed }));
    }

    [Test]
    public async Task KitsuMissingEntryCompletesAsOnTheList() {
        var (tracker, fake) = Seed(ApiName.Kitsu, Entry(500, 12, new MyListStatus()));

        await Run(tracker, Played(ApiName.Kitsu, 12));

        Assert.That(Writes(fake), Is.EqualTo(new[] { "write(id=500, progress=12, status=Completed, rewatching=False, count=-, start=False, end=True)" }));
    }

    [Test]
    public async Task ResolverStateIsNotReadAgain() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        var state = await tracker.ReadEntry(Ref(ApiName.AniList), CancellationToken.None);
        fake.Reads.Clear();

        await Run(tracker, new Resolved(Target(ApiName.AniList, 5, state: state), ResolvedBy.Ids, []));

        Assert.That(fake.Reads, Is.Empty);
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TargetWithoutStateIsReadOnce() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));

        await Run(tracker, Played(ApiName.AniList, 5));

        Assert.That(fake.Reads, Is.EqualTo(new[] { (500, false) }));
    }

    [TestCase(ApiName.AniList, "500", "")]
    [TestCase(ApiName.Shikimori, "alt-500", "")]
    [TestCase(ApiName.Simkl, "", "Fixture Show")]
    public async Task UnreadableTargetIsUnresolved(ApiName provider, string targetId, string title) {
        var (tracker, fake) = Seed(provider, Entry(500, 12, ListStatus(Status.Watching, 3)));
        fake.FailingIds.Add(500);

        var collector = await Run(tracker, Played(provider, 5));

        var outcome = collector.Outcomes.Single();
        Assert.That((outcome.Kind, outcome.Step, outcome.TargetId, outcome.TargetTitle),
            Is.EqualTo((OutcomeKind.Unresolved, "resolve", targetId, title)));
        Assert.That(fake.Writes, Is.Empty);
    }

    [Test]
    public async Task EarlierPartsRunFirst() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)),
            Entry(502, 12, ListStatus(Status.Watching, 0)));
        var resolution = new Resolved(Target(ApiName.AniList, 3, 502), ResolvedBy.SequelChain,
            [Target(ApiName.AniList, 12, 500, role: TargetRole.EarlierPart), Target(ApiName.AniList, 12, 501, role: TargetRole.EarlierPart)]);

        var collector = await Run(tracker, resolution);

        Assert.That(fake.Writes.Select(write => write.EntryId), Is.EqualTo(new[] { 500, 501, 502 }));
        Assert.That(Summary(collector), Is.EqualTo(new[] { "Confirmed:progress@500", "Confirmed:progress@501", "Confirmed:progress@502" }));
    }

    [Test]
    public async Task UnresolvedRecordsAfterItsEarlierParts() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)));
        var resolution = new Unresolved("no-cour-covers-episode", new UnresolvedRecord("500", "Fixture Show"),
            [Target(ApiName.AniList, 12, role: TargetRole.EarlierPart)]);

        var collector = await Run(tracker, resolution);

        Assert.That(Summary(collector), Is.EqualTo(new[] { "Confirmed:progress@500", "Unresolved:resolve@500" }));
    }

    [Test]
    public async Task UnresolvedWithoutARecordRecordsNothing() {
        var (tracker, fake) = Seed(ApiName.AniList);

        var collector = await Run(tracker, new Unresolved("sequel-fetch-failed", null, []));

        Assert.That(collector.Outcomes, Is.Empty);
        Assert.That(fake.Reads, Is.Empty);
    }

    [Test]
    public async Task DatesComeFromTheClock() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, null));

        await Run(tracker, Played(ApiName.AniList, 1));

        var startDate = tracker.Changes.Single().StartDate;
        Assert.That(startDate, Is.EqualTo(Now.LocalDateTime));
        Assert.That(startDate?.Kind, Is.EqualTo(DateTimeKind.Local));
    }

    [Test]
    public async Task NoCollectorStillWrites() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));

        await new Executor(tracker, Off, PlanPolicy.Automatic, null, new FixedTime(Now), NullLogger.Instance)
            .Execute(Played(ApiName.AniList, 5), CancellationToken.None);

        Assert.That(fake.Writes, Has.Count.EqualTo(1));
    }
}
