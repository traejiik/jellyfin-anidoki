using System.Linq;
using jellyfin_anidoki.Matching;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class ProgressAggregatorTests {
    private static (int Entry, int Progress)[] Playback(Match match, int played, int[] watched, params (int Id, int Total)[] entries) =>
        ProgressAggregator.ForPlayback(match, played, watched, Entries(entries)).Select(progress => (progress.Target.Entry.Id, progress.Progress)).ToArray();

    private static (int Entry, int Progress)[] WholeSeason(Match match, int[] watched, params (int Id, int Total)[] entries) =>
        ProgressAggregator.ForWholeSeason(match, watched, Entries(entries)).Select(progress => (progress.Target.Entry.Id, progress.Progress)).ToArray();

    private static int[] OneTo(int last) => Enumerable.Range(1, last).ToArray();

    [Test]
    public void HighestWatchedTargetWins() =>
        Assert.That(Playback(Shared(Range(1, 12, A)), 5, [1, 2, 5], (A, 12)), Is.EqualTo(new[] { (A, 5) }));

    [Test]
    public void AHigherWatchedEpisodeThanThePlayedOneWins() =>
        Assert.That(Playback(Shared(Range(1, 12, A)), 5, OneTo(8), (A, 12)), Is.EqualTo(new[] { (A, 8) }));

    [Test]
    public void FortyEightEpisodeSeasonReportsEachTouchedEntry() {
        int[] watched = [.. OneTo(14), 20];

        Assert.That(Playback(FourTimesTwelve(), 20, watched, (A, 12), (B, 12), (C, 12), (D, 12)), Is.EqualTo(new[] { (A, 12), (B, 8) }));
    }

    [Test]
    public void EntriesAfterThePlayedEpisodeAreUntouched() =>
        Assert.That(Playback(FourTimesTwelve(), 20, [1, 20, 30, 40], (A, 12), (B, 12), (C, 12), (D, 12)), Is.EqualTo(new[] { (A, 1), (B, 8) }));

    [Test]
    public void ThePlayedEpisodeCountsBeforeItsFlagIsSet() =>
        Assert.That(Playback(Shared(Range(1, 12, A)), 5, [1, 2], (A, 12)), Is.EqualTo(new[] { (A, 5) }));

    [Test]
    public void ATouchedEntryWithNothingWatchedHasNoProgress() =>
        Assert.That(Playback(FourTimesTwelve(), 20, [], (A, 12), (B, 12), (C, 12), (D, 12)), Is.EqualTo(new[] { (B, 8) }));

    [Test]
    public void AnIgnoredPlayedEpisodeTouchesNothing() =>
        Assert.That(Playback(Shared([Range(1, 12, A)], Skip(13)), 13, OneTo(12), (A, 12)), Is.Empty);

    [Test]
    public void APlayedEpisodeOutsideTheMatchTouchesNothing() =>
        Assert.That(Playback(Shared(Range(1, 12, A), Range(15, 26, B)), 13, OneTo(12), (A, 12), (B, 12)), Is.Empty);

    [Test]
    public void IgnoredAndUnmatchedWatchedEpisodesDoNotCount() =>
        Assert.That(Playback(Shared([Range(1, 12, A), Range(15, 26, A, 13)], Skip(5)), 2, [2, 5, 13], (A, 24)), Is.EqualTo(new[] { (A, 2) }));

    [Test]
    public void AWatchedExceptionCountsForItsEntry() =>
        Assert.That(Playback(Shared([Range(1, 12, A)], SendTo(20, A, 12)), 2, [2, 20], (A, 12)), Is.EqualTo(new[] { (A, 12) }));

    [Test]
    public void APlayedExceptionTouchesItsEntryAndTheRangesBeforeIt() =>
        Assert.That(Playback(Shared([Range(1, 12, A), Range(13, 24, B)], SendTo(25, Special, 1)), 25, OneTo(24), (A, 12), (B, 12), (Special, 1)),
            Is.EqualTo(new[] { (A, 12), (B, 12), (Special, 1) }));

    [Test]
    public void AWatchedEpisodePastTheKnownEndIsNotProgress() =>
        Assert.That(Playback(Shared(Range(1, null, A)), 5, [5, 14], (A, 12)), Is.EqualTo(new[] { (A, 5) }));

    [Test]
    public void AnEntryOfUnknownLengthHasNoEnd() =>
        Assert.That(Playback(Shared(Range(1, null, A)), 5, [5, 14], (A, 0)), Is.EqualTo(new[] { (A, 14) }));

    [Test]
    public void ManualSyncCoversEveryEntry() {
        int[] watched = [.. OneTo(14), 20, 30, 48];

        Assert.That(WholeSeason(FourTimesTwelve(), watched, (A, 12), (B, 12), (C, 12), (D, 12)),
            Is.EqualTo(new[] { (A, 12), (B, 8), (C, 6), (D, 12) }));
    }

    [Test]
    public void ManualSyncLeavesOutEntriesWithNothingWatched() =>
        Assert.That(WholeSeason(FourTimesTwelve(), [13], (A, 12), (B, 12), (C, 12), (D, 12)), Is.EqualTo(new[] { (B, 1) }));

    [Test]
    public void RepeatedAndUnorderedEpisodesDoNotChangeTheAnswer() =>
        Assert.That(WholeSeason(Shared(Range(1, 12, A)), [5, 2, 5, 3], (A, 12)), Is.EqualTo(new[] { (A, 5) }));
}
