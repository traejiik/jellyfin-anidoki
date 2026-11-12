using System;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Planning;

[TestFixture]
public class ReductionCheckTests {
    private static readonly ProviderCapabilities AniList = ProviderCapabilities.For(ApiName.AniList);

    private static EntryState Read(TrackerStatus status, int progress, bool rewatching = false) =>
        new(new EntryRef(ApiName.AniList, 1, null), 24, new ListState(status, progress, rewatching, 0), DateTimeOffset.UnixEpoch);

    [TestCase(TrackerStatus.Watching)]
    [TestCase(TrackerStatus.OnHold)]
    public void AnEntryInProgressAheadOfJellyfinIsOfferedLowerProgress(TrackerStatus status) =>
        Assert.That(ReductionCheck.Check(Read(status, 14), 8, AniList), Is.EqualTo(new LowerProgress(new EntryChange(8, status))));

    [TestCase(TrackerStatus.Completed, ReductionCheck.CompletedOnTracker)]
    [TestCase(TrackerStatus.Dropped, ReductionCheck.DroppedOnTracker)]
    public void AFinishedOrDroppedEntryKeepsTheTrackersNumber(TrackerStatus status, string reason) =>
        Assert.That(ReductionCheck.Check(Read(status, 14), 8, AniList), Is.EqualTo(new KeepTrackerProgress(reason)));

    [Test]
    public void APlanToWatchEntryWithProgressIsLeftAlone() =>
        Assert.That(ReductionCheck.Check(Read(TrackerStatus.PlanToWatch, 14), 8, AniList), Is.Null);

    [Test]
    public void ARewatchIsLeftAlone() =>
        Assert.That(ReductionCheck.Check(Read(TrackerStatus.Watching, 14, rewatching: true), 8, AniList), Is.Null);

    [Test]
    public void AStatusOnlyTrackerNeverGetsOne() =>
        Assert.That(ReductionCheck.Check(Read(TrackerStatus.Watching, 14), 8, ProviderCapabilities.For(ApiName.Annict)), Is.Null);

    [TestCase(8)]
    [TestCase(5)]
    public void AnEntryNotAheadIsLeftAlone(int progress) =>
        Assert.That(ReductionCheck.Check(Read(TrackerStatus.Watching, progress), 8, AniList), Is.Null);

    [Test]
    public void AnEntryNotOnTheListIsLeftAlone() =>
        Assert.That(ReductionCheck.Check(new EntryState(new EntryRef(ApiName.AniList, 1, null), 24, null, DateTimeOffset.UnixEpoch), 8, AniList), Is.Null);
}
