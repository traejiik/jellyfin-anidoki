using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Planning;

[TestFixture]
public class PlannerTests {
    private static readonly DateTime Now = new(2026, 10, 24, 20, 0, 0, DateTimeKind.Local);

    // one instance, so records that hold it compare equal
    private static readonly EntryTitles Titles = new("Fixture Show", null, null, []);

    private static readonly PlanRules Off = new(false, false);
    private static readonly PlanRules RewatchOn = new(false, true);
    private static readonly PlanRules PtwOn = new(true, false);

    private static ListState List(TrackerStatus status, int progress, bool rewatching = false, int? count = null) =>
        new(status, progress, rewatching, count);

    private static EntryState Entry(ApiName provider, ListState? list, int total) =>
        new(new EntryRef(provider, 500, null), total, list, DateTimeOffset.UnixEpoch, Titles);

    private static PlanInput Input(ApiName provider, ListState? list, int episode, int total, PlanRules? rules = null,
        PlanPolicy policy = PlanPolicy.Automatic) =>
        new(episode, Entry(provider, list, total), rules ?? Off, policy, ProviderCapabilities.For(provider), ProviderQuirks.For(provider), Now);

    private static PlanDecision Plan(ApiName provider, ListState? list, int episode, int total, PlanRules? rules = null,
        PlanPolicy policy = PlanPolicy.Automatic) =>
        Planner.Plan(Input(provider, list, episode, total, rules, policy));

    private static Write Expected(ApiName provider, ListState? list, int total, EntryChange change, string reason = "progress") =>
        new(change, reason, Entry(provider, list, total));

    // P1: manual marks only move progress forward.

    [Test]
    public void P1ManualAtOrBehindProgressIsNoChange() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 5), 5, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(new NoChange("manual-forward-only")));

    [Test]
    public void P1ManualCompletedIsNoChange() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Completed, 12, count: 1), 5, 12, RewatchOn, PlanPolicy.ConfirmedManual),
            Is.EqualTo(new NoChange("manual-forward-only")));

    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Mal)]
    public void P1ManualRewatchingIsNoChange(ApiName provider) =>
        Assert.That(Plan(provider, List(TrackerStatus.Completed, 3, true, 1), 5, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(new NoChange("manual-forward-only")));

    [Test]
    public void P1ManualNotOnListAdds() =>
        Assert.That(Plan(ApiName.AniList, null, 5, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(Expected(ApiName.AniList, null, 12, new EntryChange(5, TrackerStatus.Watching))));

    private static IEnumerable<ListState?> GridStates() => [
        null,
        List(TrackerStatus.PlanToWatch, 0),
        List(TrackerStatus.Watching, 3),
        List(TrackerStatus.Watching, 8),
        List(TrackerStatus.OnHold, 3),
        List(TrackerStatus.Dropped, 3),
        List(TrackerStatus.Completed, 12, count: 1),
        List(TrackerStatus.Completed, 1, count: 1),
        List(TrackerStatus.Completed, 3, count: 1),
        List(TrackerStatus.Completed, 3, true, 1)
    ];

    [Test]
    public void ManualNeverCarriesARewatch() {
        (int Episode, int Total)[] positions = [(1, 12), (5, 12), (12, 12), (1, 1), (5, 0)];
        PlanRules[] rules = [new(false, false), new(false, true), new(true, false), new(true, true)];
        foreach (var provider in Enum.GetValues<ApiName>())
            foreach (var list in GridStates())
                foreach (var (episode, total) in positions)
                    foreach (var rule in rules) {
                        var decision = Plan(provider, list, episode, total, rule, PlanPolicy.ConfirmedManual);
                        Assert.That(decision is Write { Change.Rewatch: not RewatchTransition.None }, Is.False,
                            $"{provider} {list} e={episode} n={total} {rule}: {decision}");
                    }
    }

    // P2: watching or rewatching advances.

    [Test]
    public void P2WatchingAdvances() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 3), 5, 12),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.Watching, 3), 12, new EntryChange(5, TrackerStatus.Watching))));

    [Test]
    public void P2WatchingAtOrBehindIsAlreadyWatched() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 8), 5, 12), Is.EqualTo(new NoChange("already-watched")));

    [Test]
    public void P2FirstEpisodeSetsStartDate() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 0), 1, 12),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.Watching, 0), 12, new EntryChange(1, TrackerStatus.Watching, StartDate: Now))));

    [Test]
    public void P2FirstEpisodeOfARewatchSetsStartDate() {
        var list = List(TrackerStatus.Completed, 0, true, 1);
        Assert.That(Plan(ApiName.AniList, list, 1, 12),
            Is.EqualTo(Expected(ApiName.AniList, list, 12, new EntryChange(1, TrackerStatus.Completed, RewatchTransition.Continue, StartDate: Now))));
    }

    [Test]
    public void P2LastEpisodeCompletesWithEndDate() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 8), 12, 12),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.Watching, 8), 12, new EntryChange(12, TrackerStatus.Completed, EndDate: Now))));

    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Kitsu)]
    [TestCase(ApiName.Shikimori)]
    public void P2RewatchContinues(ApiName provider) {
        var list = List(TrackerStatus.Completed, 3, true, 1);
        Assert.That(Plan(provider, list, 5, 12),
            Is.EqualTo(Expected(provider, list, 12, new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Continue))));
    }

    [Test]
    public void P2RewatchFinishesOnce() {
        var list = List(TrackerStatus.Completed, 3, true, 1);
        Assert.That(Plan(ApiName.AniList, list, 12, 12),
            Is.EqualTo(Expected(ApiName.AniList, list, 12, new EntryChange(12, TrackerStatus.Completed, RewatchTransition.Finish), "rewatch-count")));
    }

    // P3–P5: the rewatch rules.

    [TestCase(false)]
    [TestCase(true)]
    public void P3SimklCannotRewatch(bool planToWatchOnly) =>
        Assert.That(Plan(ApiName.Simkl, List(TrackerStatus.Completed, 12), 5, 12, new PlanRules(planToWatchOnly, true)),
            Is.EqualTo(new Skip("rewatch-unsupported")));

    [TestCase(false)]
    [TestCase(true)]
    public void P4RewatchOffSkips(bool planToWatchOnly) =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Completed, 12, count: 1), 5, 12, new PlanRules(planToWatchOnly, false)),
            Is.EqualTo(new Skip("rewatch-disabled")));

    [TestCase(12)]
    [TestCase(0)]
    public void P5StartsARewatchBehindProgress(int total) {
        var list = List(TrackerStatus.Completed, 12, count: 1);
        Assert.That(Plan(ApiName.AniList, list, 5, total, RewatchOn),
            Is.EqualTo(Expected(ApiName.AniList, list, total, new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Start))));
    }

    [Test]
    public void P5FinishesAtTheLastEpisode() {
        var list = List(TrackerStatus.Completed, 12, count: 1);
        Assert.That(Plan(ApiName.AniList, list, 12, 12, RewatchOn),
            Is.EqualTo(Expected(ApiName.AniList, list, 12, new EntryChange(12, TrackerStatus.Completed, RewatchTransition.Finish), "rewatch-count")));
    }

    [Test]
    public void P5SingleEpisodeFinishesAtOne() {
        var list = List(TrackerStatus.Completed, 1, count: 1);
        Assert.That(Plan(ApiName.AniList, list, 1, 1, RewatchOn),
            Is.EqualTo(Expected(ApiName.AniList, list, 1, new EntryChange(1, TrackerStatus.Completed, RewatchTransition.Finish), "rewatch-count")));
    }

    [TestCase(12)]
    [TestCase(0)]
    public void P5CompletedBehindGoesBackToWatching(int total) {
        var list = List(TrackerStatus.Completed, 3, count: 1);
        Assert.That(Plan(ApiName.AniList, list, 5, total, RewatchOn),
            Is.EqualTo(Expected(ApiName.AniList, list, total, new EntryChange(5, TrackerStatus.Watching))));
    }

    [Test]
    public void MalRewatchIsSkippedWithRewatchOff() {
        var list = List(TrackerStatus.Completed, 3, true);
        Assert.That(Plan(ApiName.Mal, list, 5, 12, Off), Is.EqualTo(new Skip("rewatch-disabled")));
        Assert.That(Plan(ApiName.AniList, list, 5, 12, Off),
            Is.EqualTo(Expected(ApiName.AniList, list, 12, new EntryChange(5, TrackerStatus.Completed, RewatchTransition.Continue))));
    }

    [TestCase(5, RewatchTransition.Continue, "progress")]
    [TestCase(1, RewatchTransition.Start, "progress")]
    [TestCase(12, RewatchTransition.Finish, "rewatch-count")]
    public void MalRewatchContinuesThroughTheRewatchRules(int episode, RewatchTransition transition, string reason) {
        var list = List(TrackerStatus.Completed, 3, true);
        Assert.That(Plan(ApiName.Mal, list, episode, 12, RewatchOn),
            Is.EqualTo(Expected(ApiName.Mal, list, 12, new EntryChange(episode, TrackerStatus.Completed, transition), reason)));
    }

    // P6–P11: the rest.

    [TestCase(12)]
    [TestCase(1)]
    public void P6OnHoldAtOrBehindIsAlreadyWatched(int total) =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.OnHold, 3), 1, total), Is.EqualTo(new NoChange("already-watched")));

    [Test]
    public void SingleEpisodeWatchingCompletesAgain() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 3), 1, 1),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.Watching, 3), 1,
                new EntryChange(1, TrackerStatus.Completed, StartDate: Now, EndDate: Now))));

    [Test]
    public void P7NotOnListWithPlanToWatchOnlySkips() =>
        Assert.That(Plan(ApiName.AniList, null, 5, 12, PtwOn), Is.EqualTo(new Skip("plan-to-watch-only")));

    [Test]
    public void KitsuMissingEntryIsWatchingAtZero() =>
        Assert.That(Plan(ApiName.Kitsu, null, 5, 12, PtwOn),
            Is.EqualTo(Expected(ApiName.Kitsu, List(TrackerStatus.Watching, 0), 12, new EntryChange(5, TrackerStatus.Watching))));

    [Test]
    public void KitsuMissingEntryCompletesWithoutStartDate() =>
        Assert.That(Plan(ApiName.Kitsu, null, 12, 12),
            Is.EqualTo(Expected(ApiName.Kitsu, List(TrackerStatus.Watching, 0), 12, new EntryChange(12, TrackerStatus.Completed, EndDate: Now))));

    [TestCase(true)]
    [TestCase(false)]
    public void P8PlanToWatchAdvances(bool planToWatchOnly) =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.PlanToWatch, 0), 5, 12, new PlanRules(planToWatchOnly, false)),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.PlanToWatch, 0), 12, new EntryChange(5, TrackerStatus.Watching))));

    [Test]
    public void PlanToWatchOnlyChecksPlanToWatchBeforeProgress() {
        var list = List(TrackerStatus.PlanToWatch, 3);
        Assert.That(Plan(ApiName.AniList, list, 1, 1, PtwOn),
            Is.EqualTo(Expected(ApiName.AniList, list, 1, new EntryChange(1, TrackerStatus.Completed, StartDate: Now, EndDate: Now))));
        Assert.That(Plan(ApiName.AniList, list, 1, 1, Off), Is.EqualTo(new NoChange("already-watched")));
    }

    [TestCase(TrackerStatus.OnHold)]
    [TestCase(TrackerStatus.Dropped)]
    public void P9OnHoldWithPlanToWatchOnlySkips(TrackerStatus status) =>
        Assert.That(Plan(ApiName.AniList, List(status, 3), 5, 12, PtwOn), Is.EqualTo(new Skip("plan-to-watch-only")));

    [TestCase(12, 12)]
    [TestCase(1, 1)]
    public void P10AddsAsCompletedAtTheLastEpisode(int episode, int total) =>
        Assert.That(Plan(ApiName.AniList, null, episode, total),
            Is.EqualTo(Expected(ApiName.AniList, null, total, new EntryChange(episode, TrackerStatus.Completed, StartDate: Now, EndDate: Now))));

    [TestCase(1, 12, true)]
    [TestCase(5, 12, false)]
    [TestCase(5, 0, false)]
    public void P10AddsAsWatching(int episode, int total, bool startDate) =>
        Assert.That(Plan(ApiName.AniList, null, episode, total),
            Is.EqualTo(Expected(ApiName.AniList, null, total, new EntryChange(episode, TrackerStatus.Watching, StartDate: startDate ? Now : null))));

    [TestCase(TrackerStatus.OnHold)]
    [TestCase(TrackerStatus.Dropped)]
    public void P11OnHoldAndDroppedGoBackToWatching(TrackerStatus status) =>
        Assert.That(Plan(ApiName.AniList, List(status, 3), 5, 12),
            Is.EqualTo(Expected(ApiName.AniList, List(status, 3), 12, new EntryChange(5, TrackerStatus.Watching))));

    // S1–S5: status-only providers.

    [Test]
    public void S1ManualCompletedIsNoChange() =>
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.Completed, 0), 5, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(new NoChange("manual-forward-only")));

    [Test]
    public void S1ManualIgnoresProgress() =>
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.Watching, 8), 5, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(new NoChange("status")));

    [Test]
    public void S2PlanToWatchUpdatesStatus() =>
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.PlanToWatch, 0), 5, 12, PtwOn),
            Is.EqualTo(Expected(ApiName.Annict, List(TrackerStatus.PlanToWatch, 0), 12, new EntryChange(5, TrackerStatus.Watching), "status")));

    private static IEnumerable<ListState?> S3States() => [null, List(TrackerStatus.Watching, 0), List(TrackerStatus.Completed, 0)];

    [TestCaseSource(nameof(S3States))]
    public void S3PlanToWatchOnlySkipsTheRest(ListState? list) =>
        Assert.That(Plan(ApiName.Annict, list, 5, 12, PtwOn), Is.EqualTo(new Skip("plan-to-watch-only")));

    [Test]
    public void S4CompletedIsRewatchDisabled() =>
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.Completed, 0), 5, 12, RewatchOn), Is.EqualTo(new Skip("rewatch-disabled")));

    [Test]
    public void S5StatusUpdate() {
        Assert.That(Plan(ApiName.Annict, null, 12, 12),
            Is.EqualTo(Expected(ApiName.Annict, null, 12, new EntryChange(12, TrackerStatus.Completed), "status")));
        Assert.That(Plan(ApiName.Annict, null, 5, 12),
            Is.EqualTo(Expected(ApiName.Annict, null, 12, new EntryChange(5, TrackerStatus.Watching), "status")));
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.Watching, 0), 5, 12), Is.EqualTo(new NoChange("status")));
        Assert.That(Plan(ApiName.Annict, null, 5, 0), Is.EqualTo(new NoChange("status")));
        Assert.That(Plan(ApiName.Annict, List(TrackerStatus.Watching, 0), 13, 12), Is.EqualTo(new NoChange("status")));
    }

    [Test]
    public void DatesComeFromTheInput() {
        var write = (Write)Plan(ApiName.AniList, null, 12, 12);
        Assert.That(new[] { write.Change.StartDate, write.Change.EndDate }, Is.All.EqualTo(Now));
    }

    [Test]
    public void ManualWriteNeverReachesTheBackstopForPlainChanges() =>
        Assert.That(Plan(ApiName.AniList, List(TrackerStatus.Watching, 3), 12, 12, policy: PlanPolicy.ConfirmedManual),
            Is.EqualTo(Expected(ApiName.AniList, List(TrackerStatus.Watching, 3), 12, new EntryChange(12, TrackerStatus.Completed, EndDate: Now))));
}
