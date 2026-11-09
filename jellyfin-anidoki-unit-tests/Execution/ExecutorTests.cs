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
using jellyfin_anidoki_unit_tests.Operations;
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

    /// <summary>Answers reads in order before falling through to the wrapped tracker.</summary>
    public Queue<EntryState?> ScriptedReads { get; } = new();

    public int ReadCount { get; private set; }

    public List<EntryState> Applied { get; } = new();

    public TrackerFailureKind? ReadFailure { get; set; }

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) {
        ReadCount++;
        if (ReadFailure is { } failure)
            throw new TrackerFailureException(failure);
        return ScriptedReads.TryDequeue(out var state) ? Task.FromResult(state) : inner.ReadEntry(entry, cancellationToken);
    }

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => inner.Search(title, cancellationToken);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) =>
        inner.GetRelations(entry, cancellationToken);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
        inner.FindByCrossIds(ids, title, kind, cancellationToken);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) {
        Changes.Add(change);
        Applied.Add(current);
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

    private static readonly ActionSource Source = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 14);

    private static async Task<List<(ProviderWriteOutcome Outcome, OutcomeDetail? Detail)>> Recorded(ITrackerProvider tracker,
        SourceResolution resolution, TimeProvider? clock = null) {
        var recorded = new List<(ProviderWriteOutcome, OutcomeDetail?)>();
        var collector = new PlaybackOutcomeCollector((outcome, detail) => recorded.Add((outcome, detail)));
        await new Executor(tracker, Off, PlanPolicy.Automatic, collector, clock ?? new FixedTime(Now), NullLogger.Instance, Source)
            .Execute(resolution, CancellationToken.None);
        return recorded;
    }

    [Test]
    public async Task AWriteRecordsItsSourceTargetAndPlan() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));

        var detail = (await Recorded(tracker, Played(ApiName.AniList, 5))).Single().Detail!;

        Assert.That((detail.SourceItem, detail.SourceEpisode, detail.TargetEpisode, detail.By, detail.Note),
            Is.EqualTo(((Guid?)Source.ItemId, (int?)14, (int?)5, (ResolvedBy?)ResolvedBy.Ids, (string?)null)));
        Assert.That(detail.PlannedChange, Is.EqualTo(tracker.Changes.Single()));
        Assert.That(detail.PlannedAgainst!.List!.Progress, Is.EqualTo(3));
        Assert.That(detail.PlannedAgainst.Titles, Is.Null);
    }

    [Test]
    public async Task ADecisionRecordsWhatItReadAndNoChange() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 8)));

        var detail = (await Recorded(tracker, Played(ApiName.AniList, 5))).Single().Detail!;

        Assert.That(detail.PlannedChange, Is.Null);
        Assert.That(detail.PlannedAgainst!.List!.Progress, Is.EqualTo(8));
    }

    [Test]
    public async Task TheSnapshotIsTheEntryAsReadNotAsTheQuirkSeesIt() {
        // Kitsu's planner quirk treats a missing entry as Watching at 0; Undo must see that it was not on the list.
        var (tracker, _) = Seed(ApiName.Kitsu, Entry(500, 12, new MyListStatus()));

        var detail = (await Recorded(tracker, Played(ApiName.Kitsu, 5))).Single().Detail!;

        Assert.That(detail.PlannedChange, Is.Not.Null);
        Assert.That(detail.PlannedAgainst!.List, Is.Null);
    }

    [Test]
    public async Task EarlierPartsRecordTheSequelChain() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        var resolution = new Resolved(Target(ApiName.AniList, 2, 501), ResolvedBy.Ids, [Target(ApiName.AniList, 12, 500, role: TargetRole.EarlierPart)]);

        var details = (await Recorded(tracker, resolution)).Select(item => item.Detail!).ToArray();

        Assert.That(details.Select(detail => (detail.By, detail.TargetEpisode)),
            Is.EqualTo(new (ResolvedBy?, int?)[] { (ResolvedBy.SequelChain, 12), (ResolvedBy.Ids, 2) }));
    }

    [Test]
    public async Task UnresolvedRecordsItsReasonAsTheNote() {
        var (tracker, _) = Seed(ApiName.AniList);

        var detail = (await Recorded(tracker, new Unresolved("no-title-match", new UnresolvedRecord("", "Fixture Show"), []))).Single().Detail!;

        Assert.That((detail.SourceEpisode, detail.TargetEpisode, detail.By, detail.Note, detail.PlannedChange),
            Is.EqualTo(((int?)14, (int?)null, (ResolvedBy?)null, (string?)"no-title-match", (EntryChange?)null)));
    }

    [Test]
    public async Task AnUnreadableTargetRecordsNotFound() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        fake.FailingIds.Add(500);

        var detail = (await Recorded(tracker, Played(ApiName.AniList, 5))).Single().Detail!;

        Assert.That((detail.Note, detail.TargetEpisode), Is.EqualTo(((string?)"not-found", (int?)5)));
    }

    [Test]
    public async Task BothPartsOfAPartialWriteCarryThePlan() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Completed, 12, count: 1)));
        fake.FailingWrites.Add(2);
        var recorded = new List<OutcomeDetail?>();
        var collector = new PlaybackOutcomeCollector((_, detail) => recorded.Add(detail));

        await new Executor(tracker, RewatchOn, PlanPolicy.Automatic, collector, new FixedTime(Now), NullLogger.Instance, Source)
            .Execute(Played(ApiName.AniList, 1), CancellationToken.None);

        Assert.That(recorded, Has.Count.EqualTo(2));
        Assert.That(recorded.Select(detail => detail!.PlannedChange), Is.All.Not.Null);
    }

    private static EntryState StateAt(int progress, int id = 500) =>
        new(Ref(ApiName.AniList, id), 12, new ListState(TrackerStatus.Watching, progress, false, null), Now,
            new EntryTitles("Fixture Show", null, null, []));

    private static Resolved PlayedFrom(EntryState state, int episode) =>
        new(Target(ApiName.AniList, episode, state: state), ResolvedBy.Ids, []);

    private static readonly MutableTime Later = new(Now.AddSeconds(61));

    [Test]
    public async Task AReadAtSixtySecondsIsStillFresh() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));

        await Recorded(tracker, PlayedFrom(StateAt(3), 5), new MutableTime(Now.AddSeconds(60)));

        Assert.That(tracker.ReadCount, Is.EqualTo(0));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AStaleUnchangedReadIsCheckedOnceThenWritten() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(3));

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(3), 5), Later);

        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
        Assert.That(recorded.Single().Outcome.Kind, Is.EqualTo(OutcomeKind.Confirmed));
    }

    [Test]
    public async Task AStaleReadThatMovedPastTheEpisodeIsReplannedAndNotWritten() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(7));

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(3), 5), Later);

        Assert.That(fake.Writes, Is.Empty);
        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        var (outcome, detail) = recorded.Single();
        Assert.That((outcome.Kind, outcome.Step), Is.EqualTo((OutcomeKind.NoChange, "already-watched")));
        Assert.That(detail!.PlannedAgainst!.List!.Progress, Is.EqualTo(7));
    }

    [Test]
    public async Task AStaleReadThatStillNeedsAWriteIsPlannedAgainstTheNewState() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(4));
        tracker.ScriptedReads.Enqueue(StateAt(4));

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(3), 5), Later);

        Assert.That(tracker.ReadCount, Is.EqualTo(2));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
        Assert.That(tracker.Applied.Single().List!.Progress, Is.EqualTo(4));
        Assert.That(recorded.Single().Detail!.PlannedAgainst!.List!.Progress, Is.EqualTo(4));
    }

    [Test]
    public async Task AnEntryThatKeepsChangingIsFailedAsStale() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(4));
        tracker.ScriptedReads.Enqueue(StateAt(4) with { List = new ListState(TrackerStatus.Watching, 4, true, null) });

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(3), 5), Later);

        Assert.That(fake.Writes, Is.Empty);
        var outcome = recorded.Single().Outcome;
        Assert.That((outcome.Kind, outcome.Step, outcome.TargetId), Is.EqualTo((OutcomeKind.Failed, "stale", "500")));
    }

    [Test]
    public async Task AStaleReadThatCannotBeRepeatedIsUnresolved() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(null);

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(3), 5), Later);

        Assert.That(fake.Writes, Is.Empty);
        Assert.That(recorded.Single().Outcome.Kind, Is.EqualTo(OutcomeKind.Unresolved));
    }

    [Test]
    public async Task AStaleDecisionWithNothingToWriteIsNotReadAgain() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 8)));

        var recorded = await Recorded(tracker, PlayedFrom(StateAt(8), 5), Later);

        Assert.That(tracker.ReadCount, Is.EqualTo(0));
        Assert.That(recorded.Single().Outcome.Kind, Is.EqualTo(OutcomeKind.NoChange));
    }

    [Test]
    public async Task EachTargetIsCheckedOnItsOwnRead() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        tracker.ScriptedReads.Enqueue(StateAt(0));
        var fresh = StateAt(0, 501) with { ReadAt = Later.Now };
        var resolution = new Resolved(Target(ApiName.AniList, 2, 501, state: fresh), ResolvedBy.SequelChain,
            [Target(ApiName.AniList, 12, 500, StateAt(0), TargetRole.EarlierPart)]);

        await Recorded(tracker, resolution, Later);

        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        Assert.That(fake.Writes, Has.Count.EqualTo(2));
    }

    [TestCase(TrackerFailureKind.Unauthorized, "unauthorized")]
    [TestCase(TrackerFailureKind.RateLimited, "rate-limited")]
    [TestCase(TrackerFailureKind.Transient, "transient")]
    [TestCase(TrackerFailureKind.Rejected, "rejected")]
    public async Task AFailedReadIsRecordedAndStopsTheProvider(TrackerFailureKind kind, string step) {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        tracker.ReadFailure = kind;
        var resolution = new Resolved(Target(ApiName.AniList, 2, 501), ResolvedBy.SequelChain, [Target(ApiName.AniList, 12, 500, role: TargetRole.EarlierPart)]);

        var collector = await Run(tracker, resolution);

        Assert.That(Summary(collector), Is.EqualTo(new[] { $"Failed:{step}@500" }));
        Assert.That(fake.Writes, Is.Empty);
        Assert.That(collector.Outcomes.Single().TargetTitle, Is.EqualTo("Fixture Show"));
    }

    [Test]
    public async Task AWriteTheTrackerRefusedIsFailedNotUnconfirmed() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new TrackerFailureException(TrackerFailureKind.Rejected);

        var collector = await Run(tracker, Played(ApiName.AniList, 5));

        Assert.That(Summary(collector), Is.EqualTo(new[] { "Failed:rejected@500" }));
    }

    [Test]
    public async Task AFailureRecordsTheTargetAndPlan() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new TrackerFailureException(TrackerFailureKind.Transient);

        var detail = (await Recorded(tracker, Played(ApiName.AniList, 5))).Single().Detail!;

        Assert.That((detail.TargetEpisode, detail.By), Is.EqualTo(((int?)5, (ResolvedBy?)ResolvedBy.Ids)));
        Assert.That(detail.PlannedChange, Is.Not.Null);
    }

    [Test]
    public async Task AWriteToAMissingEntryIsUnresolvedAndTheNextTargetRuns() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        int calls = 0;
        tracker.OnApply = (current, _) => ++calls == 1
            ? throw new TrackerFailureException(TrackerFailureKind.NotFound)
            : Task.FromResult<WriteReceipt?>(new WriteReceipt(2, TrackerStatus.Watching, null, null, true));
        var resolution = new Resolved(Target(ApiName.AniList, 2, 501), ResolvedBy.SequelChain, [Target(ApiName.AniList, 12, 500, role: TargetRole.EarlierPart)]);

        var recorded = await Recorded(tracker, resolution);

        Assert.That(recorded.Select(item => $"{item.Outcome.Kind}:{item.Outcome.Step}"), Is.EqualTo(new[] { "Unresolved:resolve", "Confirmed:progress" }));
        Assert.That(recorded[0].Detail!.Note, Is.EqualTo("not-found"));
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public void CancellationIsNotAFailure() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new OperationCanceledException();

        Assert.That(async () => await Run(tracker, Played(ApiName.AniList, 5)), Throws.InstanceOf<OperationCanceledException>());
    }

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

    [TestCase(ApiName.Kitsu)]
    [TestCase(ApiName.Shikimori)]
    public async Task AFinishIsConfirmedWhenTheReceiptDoesNotEchoTheCount(ApiName provider) {
        var (tracker, _) = Seed(provider, Entry(500, 12, ListStatus(Status.Completed, 12, count: 3)));
        tracker.OnApply = (_, _) => Task.FromResult<WriteReceipt?>(new WriteReceipt(12, TrackerStatus.Completed, null, null, true));

        var collector = await Run(tracker, Played(provider, 12), RewatchOn);

        Assert.That(collector.Outcomes.Select(outcome => $"{outcome.Kind}:{outcome.Step}"), Is.EqualTo(new[] { "Confirmed:rewatch-count" }));
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

    private static Executor NewExecutor(ITrackerProvider tracker, PlaybackOutcomeCollector? collector = null, TimeProvider? clock = null) =>
        new(tracker, Off, PlanPolicy.Automatic, collector, clock ?? new FixedTime(Now), NullLogger.Instance, Source);

    private static (Executor Executor, List<(ProviderWriteOutcome Outcome, OutcomeDetail? Detail)> Recorded) Recording(ITrackerProvider tracker,
        TimeProvider? clock = null) {
        var recorded = new List<(ProviderWriteOutcome Outcome, OutcomeDetail? Detail)>();
        return (NewExecutor(tracker, new PlaybackOutcomeCollector((outcome, detail) => recorded.Add((outcome, detail))), clock), recorded);
    }

    private static async Task<PlannedTarget> PlanOne(Executor executor, SourceResolution resolution) =>
        (PlannedTarget)(await executor.Plan(resolution, CancellationToken.None)).Single();

    private static Resolved TwoParts() =>
        new(Target(ApiName.AniList, 2, 501), ResolvedBy.SequelChain, [Target(ApiName.AniList, 12, 500, role: TargetRole.EarlierPart)]);

    [Test]
    public async Task PlanReadsAndDecidesWithoutWritingOrRecording() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new AssertionException("Plan must not write");
        var collector = new PlaybackOutcomeCollector();

        var planned = await PlanOne(NewExecutor(tracker, collector), Played(ApiName.AniList, 5));

        Assert.That(planned.Decision, Is.InstanceOf<Write>());
        Assert.That(((Write)planned.Decision).Change.Progress, Is.EqualTo(5));
        Assert.That(planned.Read.List!.Progress, Is.EqualTo(3));
        Assert.That((fake.Writes.Count, collector.Outcomes.Length, tracker.ReadCount), Is.EqualTo((0, 0, 1)));
    }

    [Test]
    public async Task PlanListsEarlierPartsBeforeThePlayedTarget() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));

        var steps = await NewExecutor(tracker).Plan(TwoParts(), CancellationToken.None);

        Assert.That(steps.Cast<PlannedTarget>().Select(step => (step.Trace.Target.Entry.Id, step.Trace.Target.Role, step.Trace.By)),
            Is.EqualTo(new[] { (500, TargetRole.EarlierPart, ResolvedBy.SequelChain), (501, TargetRole.Played, ResolvedBy.SequelChain) }));
    }

    [Test]
    public async Task PlanReturnsWhatExecuteRecordsForUnresolvedAndIgnored() {
        var (tracker, _) = Seed(ApiName.AniList);
        SourceResolution[] resolutions = [
            new Unresolved("no-title-match", new UnresolvedRecord("", "Fixture Show"), []),
            new Ignored(ResolvedBy.SharedMatch, 3, "Fixture Show")
        ];

        foreach (SourceResolution resolution in resolutions) {
            var executed = (await Recorded(tracker, resolution)).Single();
            var planned = (PlannedOutcome)(await NewExecutor(tracker).Plan(resolution, CancellationToken.None)).Single();

            Assert.That((planned.Outcome, planned.Detail), Is.EqualTo(executed));
            Assert.That(planned.StopsProvider, Is.False);
        }
    }

    [Test]
    public async Task PlanStopsAfterAReadFailureThatStopsTheProvider() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        tracker.ReadFailure = TrackerFailureKind.RateLimited;

        var steps = await NewExecutor(tracker).Plan(TwoParts(), CancellationToken.None);

        var stopped = (PlannedOutcome)steps.Single();
        Assert.That((stopped.Outcome.Kind, stopped.Outcome.Step, stopped.Outcome.TargetId, stopped.StopsProvider),
            Is.EqualTo((OutcomeKind.Failed, "rate-limited", "500", true)));
    }

    [Test]
    public async Task PlanKeepsGoingPastAnUnreadableTarget() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 0)), Entry(501, 12, ListStatus(Status.Watching, 0)));
        tracker.ScriptedReads.Enqueue(null);

        var steps = await NewExecutor(tracker).Plan(TwoParts(), CancellationToken.None);

        Assert.That(steps.Select(step => step.GetType().Name), Is.EqualTo(new[] { nameof(PlannedOutcome), nameof(PlannedTarget) }));
        Assert.That(((PlannedOutcome)steps[0]).Outcome.Kind, Is.EqualTo(OutcomeKind.Unresolved));
    }

    [TestCase(3, 5)]
    [TestCase(8, 5)]
    public async Task PlanThenApplyRecordsWhatExecuteRecords(int progress, int episode) {
        var (first, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, progress)));
        var (second, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, progress)));
        var executed = await Recorded(first, Played(ApiName.AniList, episode));
        var (executor, applied) = Recording(second);

        foreach (PlanStep step in await executor.Plan(Played(ApiName.AniList, episode), CancellationToken.None))
            Assert.That(await executor.ApplyPlanned((PlannedTarget)step, ApplyMode.Automatic, CancellationToken.None), Is.True);

        Assert.That(applied.Select(item => (item.Outcome, item.Detail?.PlannedChange, item.Detail?.PlannedAgainst?.List)),
            Is.EqualTo(executed.Select(item => (item.Outcome, item.Detail?.PlannedChange, item.Detail?.PlannedAgainst?.List))));
    }

    [TestCase(TrackerFailureKind.RateLimited, false)]
    [TestCase(TrackerFailureKind.NotFound, true)]
    public async Task ApplyPlannedSaysWhetherTheProviderCanContinue(TrackerFailureKind kind, bool continues) {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.OnApply = (_, _) => throw new TrackerFailureException(kind);
        var executor = NewExecutor(tracker);
        var planned = await PlanOne(executor, Played(ApiName.AniList, 5));

        Assert.That(await executor.ApplyPlanned(planned, ApplyMode.Automatic, CancellationToken.None), Is.EqualTo(continues));
    }

    [Test]
    public async Task ApplyPlannedWithACancelledTokenWritesNothing() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        var executor = NewExecutor(tracker);
        var planned = await PlanOne(executor, Played(ApiName.AniList, 5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await executor.ApplyPlanned(planned, ApplyMode.Automatic, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fake.Writes, Is.Empty);
    }

    [Test]
    public void ResolutionFailureOutcomeIsWhatRecordResolutionFailureRecords() {
        var (tracker, _) = Seed(ApiName.AniList);
        var (executor, recorded) = Recording(tracker);
        var failure = new TrackerFailureException(TrackerFailureKind.Unauthorized);

        executor.RecordResolutionFailure(failure, "Fixture Show");
        var outcome = executor.ResolutionFailureOutcome(failure, "Fixture Show");

        Assert.That((outcome.Outcome, outcome.Detail), Is.EqualTo(recorded.Single()));
        Assert.That(outcome.StopsProvider, Is.True);
    }

    [Test]
    public async Task StrictRereadsEvenAFreshPlanAndWritesWhenNothingMoved() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(3));
        var (executor, recorded) = Recording(tracker);
        var planned = await PlanOne(executor, PlayedFrom(StateAt(3), 5));

        Assert.That(await executor.ApplyPlanned(planned, ApplyMode.Strict, CancellationToken.None), Is.True);

        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
        Assert.That(recorded.Single().Outcome.Kind, Is.EqualTo(OutcomeKind.Confirmed));
    }

    [TestCase(4)]
    [TestCase(7)]
    public async Task StrictRefusesAnEntryThatMovedSincePlanning(int progressNow) {
        // 7 is past the episode: automatic mode would re-plan it into NoChange, strict mode still refuses it.
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(progressNow));
        var (executor, recorded) = Recording(tracker);
        var planned = await PlanOne(executor, PlayedFrom(StateAt(3), 5));

        Assert.That(await executor.ApplyPlanned(planned, ApplyMode.Strict, CancellationToken.None), Is.True);

        Assert.That(fake.Writes, Is.Empty);
        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        var (outcome, detail) = recorded.Single();
        Assert.That((outcome.Kind, outcome.Step, outcome.TargetId), Is.EqualTo((OutcomeKind.Skipped, "preview-stale", "500")));
        Assert.That(detail!.PlannedAgainst!.List!.Progress, Is.EqualTo(progressNow));
    }

    [Test]
    public async Task StrictTreatsAnUnreadableEntryAsUnresolved() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(null);
        var (executor, recorded) = Recording(tracker);
        var planned = await PlanOne(executor, PlayedFrom(StateAt(3), 5));

        await executor.ApplyPlanned(planned, ApplyMode.Strict, CancellationToken.None);

        Assert.That(fake.Writes, Is.Empty);
        Assert.That(recorded.Single().Outcome.Kind, Is.EqualTo(OutcomeKind.Unresolved));
    }

    [Test]
    public async Task StrictRecordsADecisionWithNothingToWriteWithoutReading() {
        var (tracker, _) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 8)));
        var (executor, recorded) = Recording(tracker);
        var planned = await PlanOne(executor, PlayedFrom(StateAt(8), 5));

        await executor.ApplyPlanned(planned, ApplyMode.Strict, CancellationToken.None);

        Assert.That(tracker.ReadCount, Is.EqualTo(0));
        Assert.That((recorded.Single().Outcome.Kind, recorded.Single().Outcome.Step), Is.EqualTo((OutcomeKind.NoChange, "already-watched")));
    }

    [Test]
    public async Task StrictReadsOnceEvenWhenThePlanIsOlderThanSixtySeconds() {
        var (tracker, fake) = Seed(ApiName.AniList, Entry(500, 12, ListStatus(Status.Watching, 3)));
        tracker.ScriptedReads.Enqueue(StateAt(3));
        var (executor, _) = Recording(tracker, Later);
        var planned = await PlanOne(executor, PlayedFrom(StateAt(3), 5));

        await executor.ApplyPlanned(planned, ApplyMode.Strict, CancellationToken.None);

        Assert.That(tracker.ReadCount, Is.EqualTo(1));
        Assert.That(fake.Writes, Has.Count.EqualTo(1));
    }
}
