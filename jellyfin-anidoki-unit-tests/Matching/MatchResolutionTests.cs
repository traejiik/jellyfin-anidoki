using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchResolutionTests {
    private const string Title = "Fixture Show";

    private static void AssertPlayed(SourceResolution resolution, int entry, int episode) {
        Assert.That(resolution, Is.TypeOf<Resolved>(), () => resolution.ToString());
        var played = ((Resolved)resolution).Played;
        Assert.That((played.Entry.Id, played.TargetEpisode, played.Role), Is.EqualTo((entry, episode, TargetRole.Played)));
    }

    private static void AssertUnresolved(SourceResolution resolution, string reason, UnresolvedRecord record) {
        Assert.That(resolution, Is.TypeOf<Unresolved>(), () => resolution.ToString());
        var unresolved = (Unresolved)resolution;
        Assert.That((unresolved.Reason, unresolved.Record), Is.EqualTo((reason, record)));
        Assert.That(unresolved.EarlierParts, Is.Empty);
    }

    [TestCase(1, A, 1)]
    [TestCase(12, A, 12)]
    [TestCase(13, B, 1)]
    [TestCase(37, D, 1)]
    [TestCase(48, D, 12)]
    public void FortyEightEpisodesResolveToFourTwelveEpisodeEntries(int episode, int entry, int target) =>
        AssertPlayed(RangeResolver.Resolve(FourTimesTwelve(), episode, Title, FourEntries()), entry, target);

    // Shokofin merges several AniDB series into one season, so one source key holds all four parts.
    [Test]
    public void MergedSeasonCompletesEveryPartBeforeThePlayedOne() {
        var resolution = RangeResolver.Resolve(FourTimesTwelve(), 37, Title, FourEntries());

        AssertPlayed(resolution, D, 1);
        Assert.That(resolution.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode, part.Role)), Is.EqualTo(new[] {
            (A, 12, TargetRole.EarlierPart), (B, 12, TargetRole.EarlierPart), (C, 12, TargetRole.EarlierPart)
        }));
    }

    [TestCase(1, A, 1)]
    [TestCase(12, A, 12)]
    [TestCase(13, B, 1)]
    [TestCase(25, B, 13)]
    [TestCase(26, C, 1)]
    [TestCase(36, C, 11)]
    [TestCase(37, D, 1)]
    [TestCase(48, D, 12)]
    public void VariableLengthRanges(int episode, int entry, int target) {
        var match = Shared(Range(1, 12, A), Range(13, 25, B), Range(26, 36, C), Range(37, 48, D));

        AssertPlayed(RangeResolver.Resolve(match, episode, Title, Entries((A, 12), (B, 13), (C, 11), (D, 12))), entry, target);
    }

    [Test]
    public void GapLeavesEpisodesUnmatched() {
        var match = Shared(Range(1, 12, A), Range(15, 26, B));
        var entries = Entries((A, 12), (B, 12));

        AssertUnresolved(RangeResolver.Resolve(match, 13, Title, entries), "not-in-match", new UnresolvedRecord("", Title));
        AssertPlayed(RangeResolver.Resolve(match, 15, Title, entries), B, 1);
    }

    [Test]
    public void ExceptionWinsOverRange() {
        var match = Shared([Range(1, 36, A)], Skip(13), SendTo(25, Special, 1));
        var entries = Entries((A, 36), (Special, 1));

        Assert.That(RangeResolver.Resolve(match, 13, Title, entries), Is.TypeOf<Ignored>());
        AssertPlayed(RangeResolver.Resolve(match, 25, Title, entries), Special, 1);
        AssertPlayed(RangeResolver.Resolve(match, 26, Title, entries), A, 26);
    }

    [Test]
    public void SpecialsSeasonZeroByExceptions() {
        var match = new Match(new MatchScope(ApiName.AniList, "season:00000000000000000000000000000001:0"), [],
            [SendTo(0, Special, 1), SendTo(1, C, 2)]);
        var entries = Entries((Special, 1), (C, 2));

        AssertPlayed(RangeResolver.Resolve(match, 0, Title, entries), Special, 1);
        AssertPlayed(RangeResolver.Resolve(match, 1, Title, entries), C, 2);
        AssertUnresolved(RangeResolver.Resolve(match, 2, Title, entries), "not-in-match", new UnresolvedRecord("", Title));
    }

    [Test]
    public void OpenRangeForAiringEntry() =>
        AssertPlayed(RangeResolver.Resolve(Shared(Range(1, null, A)), 40, Title, Entries((A, 0))), A, 40);

    [Test]
    public void OpenRangePastKnownTotalIsPastEntryEnd() {
        var match = Shared(Range(1, null, A));
        var entries = Entries((A, 12));

        AssertPlayed(RangeResolver.Resolve(match, 12, Title, entries), A, 12);
        AssertUnresolved(RangeResolver.Resolve(match, 13, Title, entries), "past-entry-end", new UnresolvedRecord(A.ToString(), $"Entry {A}"));
    }

    [Test]
    public void ClosedRangeSavedBeforeTheLengthWasKnownIsPastEntryEnd() =>
        AssertUnresolved(RangeResolver.Resolve(Shared(Range(1, 13, A)), 13, Title, Entries((A, 12))), "past-entry-end",
            new UnresolvedRecord(A.ToString(), $"Entry {A}"));

    [Test]
    public void LengthDisagreementThirteenVersusTwelve() {
        var match = Shared([Range(1, 12, A)], Skip(13));
        var entries = Entries((A, 12));

        AssertPlayed(RangeResolver.Resolve(match, 12, Title, entries), A, 12);
        var ignored = RangeResolver.Resolve(match, 13, Title, entries);
        Assert.That(ignored, Is.TypeOf<Ignored>());
        Assert.That(ignored.EarlierParts, Is.Empty);
    }

    [Test]
    public void MovieMatch() {
        var match = new Match(new MatchScope(ApiName.AniList, "movie:00000000000000000000000000000002"), [Range(1, 1, A)], []);

        var resolution = RangeResolver.Resolve(match, 1, Title, Entries((A, 1)));

        AssertPlayed(resolution, A, 1);
        Assert.That(resolution.EarlierParts, Is.Empty);
    }

    [Test]
    public void EachProviderKeepsItsOwnSegmentation() {
        var aniList = FourTimesTwelve();
        var mal = new Match(new MatchScope(ApiName.Mal, SourceKey), [new MatchRange(1, 48, new MatchTarget(new EntryRef(ApiName.Mal, 7, null), "Whole"), 1)], []);
        var malEntries = new[] { new EntryState(new EntryRef(ApiName.Mal, 7, null), 48, null, DateTimeOffset.UnixEpoch) }.ToDictionary(state => state.Entry);

        AssertPlayed(RangeResolver.Resolve(aniList, 13, Title, FourEntries()), B, 1);
        AssertPlayed(RangeResolver.Resolve(mal, 13, Title, malEntries), 7, 13);
    }

    [Test]
    public void AnEntryInTwoRangesIsAnEarlierPartOnlyOnceAtItsHighestEpisode() {
        var match = Shared(Range(1, 12, A), Range(13, 13, B), Range(14, 25, A, 13), Range(26, 38, C));

        var resolution = RangeResolver.Resolve(match, 26, Title, Entries((A, 24), (B, 1), (C, 13)));

        Assert.That(resolution.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode)), Is.EqualTo(new[] { (A, 24), (B, 1) }));
    }

    [Test]
    public void TheEntryBeingPlayedIsNotAlsoAnEarlierPart() {
        var match = Shared(Range(1, 12, A), Range(15, 26, A, 13));

        Assert.That(RangeResolver.Resolve(match, 20, Title, Entries((A, 24))).EarlierParts, Is.Empty);
    }

    [Test]
    public void AnEarlierPartStopsAtTheLengthItNowHas() {
        var match = Shared(Range(1, 13, A), Range(14, 25, B));

        var resolution = RangeResolver.Resolve(match, 14, Title, Entries((A, 12), (B, 12)));

        Assert.That(resolution.EarlierParts.Single().TargetEpisode, Is.EqualTo(12));
    }

    [Test]
    public void AnUnreadableEarlierPartIsLeftForTheExecutor() {
        var resolution = RangeResolver.Resolve(FourTimesTwelve(), 13, Title, Entries((B, 12)));

        AssertPlayed(resolution, B, 1);
        var part = resolution.EarlierParts.Single();
        Assert.That((part.Entry.Id, part.TargetEpisode, part.State), Is.EqualTo((A, 12, (EntryState?)null)));
    }

    [Test]
    public void ATargetFoundByCrossIdsRecordsNoNumericId() {
        var entry = new EntryRef(ApiName.Simkl, 5, null, new CrossReference(new ResolvedIds(1, 2, 3, 4), "Show", MediaKind.Series));
        var match = new Match(new MatchScope(ApiName.Simkl, SourceKey), [new MatchRange(1, 12, new MatchTarget(entry, "Show"), 1)], []);

        AssertUnresolved(RangeResolver.Resolve(match, 1, Title, Entries()), "match-target-unreadable", new UnresolvedRecord("", "Show"));
    }

    [Test]
    public void ASavedMatchRecordsItsRevisionAndWhoseItIs() {
        var match = FourTimesTwelve() with { Revision = 7 };

        var resolved = (Resolved)RangeResolver.Resolve(match, 13, Title, FourEntries());

        Assert.That((resolved.By, resolved.Played.MatchRevision, resolved.EarlierParts.Single().MatchRevision),
            Is.EqualTo((ResolvedBy.SharedMatch, (long?)7, (long?)7)));
    }

    [Test]
    public void AnUnsavedMatchRecordsNoRevision() =>
        Assert.That(((Resolved)RangeResolver.Resolve(FourTimesTwelve(), 1, Title, FourEntries())).Played.MatchRevision, Is.Null);

    [Test]
    public void AnIgnoredEpisodeOfAPersonalMatchSaysSo() {
        var match = new Match(new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid()), [], [Skip(1)], 3);

        var ignored = (Ignored)RangeResolver.Resolve(match, 1, Title, Entries());

        Assert.That((ignored.By, ignored.MatchRevision), Is.EqualTo((ResolvedBy.PersonalMatch, (long?)3)));
    }

    [Test]
    public void TargetsListsEachEntryOnce() {
        var match = Shared([Range(1, 12, A), Range(13, 24, A, 13)], SendTo(25, Special, 1), SendTo(26, A, 25), Skip(27));

        Assert.That(match.Targets.Select(target => target.Entry.Id), Is.EqualTo(new[] { A, Special }));
    }
}
