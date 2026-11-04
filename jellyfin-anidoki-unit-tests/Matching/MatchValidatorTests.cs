using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchValidatorTests {
    private static string[] Errors(Match match, params (int Id, int Total)[] entries) =>
        MatchValidator.Validate(match, MediaKind.Series, Entries(entries)).Errors.Select(issue => issue.Rule).Distinct().ToArray();

    private static string[] Warnings(Match match, params (int Id, int Total)[] entries) =>
        MatchValidator.Validate(match, MediaKind.Series, Entries(entries)).Warnings.Select(issue => issue.Rule).Distinct().ToArray();

    private static string[] MovieErrors(Match match) =>
        MatchValidator.Validate(match, MediaKind.Movie, Entries((A, 1))).Errors.Select(issue => issue.Rule).ToArray();

    [Test]
    public void AValidMatchHasNoIssues() {
        var result = MatchValidator.Validate(FourTimesTwelve(), MediaKind.Series, FourEntries());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void V1NeedsARangeOrAnException() => Assert.That(Errors(Shared()), Is.EqualTo(new[] { "V1" }));

    [Test]
    public void V1IsMetByASingleException() => Assert.That(Errors(Shared([], Skip(1))), Is.Empty);

    [Test]
    public void V2RejectsANegativeSourceStart() => Assert.That(Errors(Shared(Range(-1, 12, A)), (A, 24)), Is.EqualTo(new[] { "V2" }));

    [Test]
    public void V2AcceptsEpisodeZero() => Assert.That(Errors(Shared(Range(0, 11, A)), (A, 12)), Is.Empty);

    [Test]
    public void V2RejectsARangeRunningBackwards() => Assert.That(Errors(Shared(Range(12, 1, A)), (A, 24)), Is.EqualTo(new[] { "V2" }));

    [Test]
    public void V2RejectsAnOpenRangeThatIsNotLast() =>
        Assert.That(Errors(Shared(Range(1, null, A), Range(13, 24, B)), (A, 12), (B, 12)), Is.EqualTo(new[] { "V2" }));

    [Test]
    public void V2RejectsAnEpisodeNumberTooHighToAddUp() =>
        Assert.That(Errors(Shared(Range(1, int.MaxValue, A, 2)), (A, 0)), Is.EqualTo(new[] { "V2" }));

    [Test]
    public void V2RejectsANegativeExceptionEpisode() => Assert.That(Errors(Shared([], Skip(-1))), Is.EqualTo(new[] { "V2" }));

    [Test]
    public void V3RejectsOverlappingRanges() =>
        Assert.That(Errors(Shared(Range(1, 13, A), Range(13, 24, B)), (A, 13), (B, 12)), Is.EqualTo(new[] { "V3" }));

    [Test]
    public void V3FindsAnOverlapWhateverOrderTheRangesAreIn() =>
        Assert.That(Errors(Shared(Range(13, 24, B), Range(1, 13, A)), (A, 13), (B, 12)), Is.EqualTo(new[] { "V3" }));

    [Test]
    public void V3RejectsARangeStartingInsideAnOpenRange() =>
        Assert.That(Errors(Shared(Range(13, 24, B), Range(1, null, A)), (A, 0), (B, 12)), Is.EqualTo(new[] { "V3" }));

    [Test]
    public void V3RejectsTwoExceptionsForOneEpisode() =>
        Assert.That(Errors(Shared([], Skip(1), SendTo(1, A, 1)), (A, 12)), Is.EqualTo(new[] { "V3" }));

    [Test]
    public void V4RejectsATargetBelowOne() => Assert.That(Errors(Shared(Range(1, 12, A, 0)), (A, 12)), Is.EqualTo(new[] { "V4" }));

    [Test]
    public void V4RejectsATargetPastTheKnownTotal() => Assert.That(Errors(Shared(Range(1, 13, A)), (A, 12)), Is.EqualTo(new[] { "V4" }));

    [Test]
    public void V4AcceptsAnyLengthWhileTheTotalIsUnknown() => Assert.That(Errors(Shared(Range(1, 500, A)), (A, 0)), Is.Empty);

    [Test]
    public void V4RejectsAnOpenRangeStartingPastTheKnownTotal() =>
        Assert.That(Errors(Shared(Range(1, null, A, 13)), (A, 12)), Is.EqualTo(new[] { "V4" }));

    [Test]
    public void V4AcceptsAnOpenRangeInsideTheKnownTotal() => Assert.That(Errors(Shared(Range(1, null, A)), (A, 12)), Is.Empty);

    [Test]
    public void V4ChecksExceptionTargets() {
        Assert.That(Errors(Shared([], SendTo(1, A, 0)), (A, 12)), Is.EqualTo(new[] { "V4" }));
        Assert.That(Errors(Shared([], SendTo(1, A, 13)), (A, 12)), Is.EqualTo(new[] { "V4" }));
    }

    [Test]
    public void V5RejectsTwoRangesReachingOneTarget() =>
        Assert.That(Errors(Shared(Range(1, 12, A), Range(15, 26, A, 12)), (A, 24)), Is.EqualTo(new[] { "V5" }));

    [Test]
    public void V5RejectsAnExceptionLandingOnARangesTarget() =>
        Assert.That(Errors(Shared([Range(1, 12, A)], SendTo(20, A, 5)), (A, 12)), Is.EqualTo(new[] { "V5" }));

    [Test]
    public void V5RejectsTwoExceptionsLandingOnOneTarget() =>
        Assert.That(Errors(Shared([], SendTo(1, A, 5), SendTo(2, A, 5)), (A, 12)), Is.EqualTo(new[] { "V5" }));

    [Test]
    public void V5AllowsAnExceptionWhenTheRangesOwnEpisodeHasLeftIt() =>
        Assert.That(Errors(Shared([Range(1, 12, A)], Skip(5), SendTo(20, A, 5)), (A, 12)), Is.Empty);

    [Test]
    public void V5AllowsRangesThatOverlapOnlyWhereAnEpisodeHasLeft() =>
        Assert.That(Errors(Shared([Range(1, 12, A), Range(15, 26, A, 12)], Skip(12)), (A, 24)), Is.Empty);

    [Test]
    public void V5ChecksVeryWideRangesQuickly() =>
        Assert.That(Errors(Shared(Range(1, MatchValidator.MaxEpisodeNumber - 1, A), Range(MatchValidator.MaxEpisodeNumber, null, A)), (A, 0)), Is.EqualTo(new[] { "V5" }));

    [Test]
    public void V6RejectsATargetThatCouldNotBeRead() => Assert.That(Errors(Shared(Range(1, 12, A)), (B, 12)), Is.EqualTo(new[] { "V6" }));

    [Test]
    public void V6ChecksExceptionTargetsToo() => Assert.That(Errors(Shared([], SendTo(1, A, 1))), Is.EqualTo(new[] { "V6" }));

    [Test]
    public void V6ReadsCrossIdEntriesByTheirWholeReference() {
        var entry = new EntryRef(ApiName.Simkl, 5, null, new CrossReference(new ResolvedIds(1, 2, 3, 4), "Show", MediaKind.Series));
        var match = new Match(new MatchScope(ApiName.Simkl, SourceKey), [new MatchRange(1, 12, new MatchTarget(entry, "Show"), 1)], []);
        var read = new[] { new EntryState(entry with { Lookup = entry.Lookup! with { } }, 12, null, DateTimeOffset.UnixEpoch) }.ToDictionary(state => state.Entry);

        Assert.That(MatchValidator.Validate(match, MediaKind.Series, read).IsValid, Is.True);
    }

    [Test]
    public void V7AcceptsAMovieWithOneRange() => Assert.That(MovieErrors(Shared(Range(1, 1, A))), Is.Empty);

    [Test]
    public void V7AcceptsAMovieWithOneException() => Assert.That(MovieErrors(Shared([], SendTo(1, A, 1))), Is.Empty);

    [Test]
    public void V7RejectsAMovieWithALongerRange() => Assert.That(MovieErrors(Shared(Range(1, 2, A, 0))), Does.Contain("V7"));

    [Test]
    public void V7RejectsAMovieWithARangeAndAnException() => Assert.That(MovieErrors(Shared([Range(1, 1, A)], Skip(2))), Does.Contain("V7"));

    [Test]
    public void V7RejectsAMovieExceptionForAnotherEpisode() => Assert.That(MovieErrors(Shared([], Skip(2))), Does.Contain("V7"));

    [Test]
    public void V7DoesNotApplyToASeason() => Assert.That(Errors(Shared(Range(1, 2, A)), (A, 2)), Is.Empty);

    [Test]
    public void V8LimitsRanges() {
        var ranges = Enumerable.Range(0, MatchValidator.MaxRanges + 1).Select(index => Range(index + 1, index + 1, A, index + 1)).ToArray();

        Assert.That(Errors(Shared(ranges), (A, 100)), Is.EqualTo(new[] { "V8" }));
        Assert.That(Errors(Shared(ranges[..^1]), (A, 100)), Is.Empty);
    }

    [Test]
    public void V8LimitsExceptions() {
        var exceptions = Enumerable.Range(1, MatchValidator.MaxExceptions + 1).Select(episode => (MatchException)Skip(episode)).ToArray();

        Assert.That(Errors(Shared([], exceptions)), Is.EqualTo(new[] { "V8" }));
        Assert.That(Errors(Shared([], exceptions[..^1])), Is.Empty);
    }

    [Test]
    public void W1WarnsAboutAGap() {
        var match = Shared(Range(1, 12, A), Range(15, 26, B));

        Assert.That(Warnings(match, (A, 12), (B, 12)), Is.EqualTo(new[] { "W1" }));
        Assert.That(Errors(match, (A, 12), (B, 12)), Is.Empty);
    }

    [Test]
    public void W1StaysQuietWhenExceptionsFillTheGap() =>
        Assert.That(Warnings(Shared([Range(1, 12, A), Range(15, 26, B)], Skip(13), Skip(14)), (A, 12), (B, 12)), Is.Empty);

    [Test]
    public void W1StillWarnsWhenExceptionsFillOnlyPartOfTheGap() =>
        Assert.That(Warnings(Shared([Range(1, 12, A), Range(15, 26, B)], Skip(13)), (A, 12), (B, 12)), Is.EqualTo(new[] { "W1" }));

    [Test]
    public void W2WarnsAboutAnUnknownTotal() => Assert.That(Warnings(Shared(Range(1, null, A)), (A, 0)), Is.EqualTo(new[] { "W2" }));

    [Test]
    public void W3WarnsWhenTheFirstRangeStartsLate() => Assert.That(Warnings(Shared(Range(5, 16, A)), (A, 12)), Is.EqualTo(new[] { "W3" }));

    [Test]
    public void W3StaysQuietForARangeFromEpisodeZero() => Assert.That(Warnings(Shared(Range(0, 11, A)), (A, 12)), Is.Empty);

    [Test]
    public void W3WarnsWhenAnEntryDoesNotStartAtOne() => Assert.That(Warnings(Shared(Range(1, 6, A, 7)), (A, 12)), Is.EqualTo(new[] { "W3" }));

    [Test]
    public void W3WarnsWhenTargetEpisodesAreSkipped() =>
        Assert.That(Warnings(Shared(Range(1, 6, A), Range(7, 12, A, 9)), (A, 14)), Is.EqualTo(new[] { "W3" }));

    [Test]
    public void W4WarnsWhenTheSharedMatchMoved() {
        var match = new Match(new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid()), [Range(1, 12, A)], [], 1, BaseSharedRevision: 2);

        Assert.That(MatchValidator.Validate(match, MediaKind.Series, Entries((A, 12)), currentSharedRevision: 3).Warnings.Single().Rule, Is.EqualTo("W4"));
        Assert.That(MatchValidator.Validate(match, MediaKind.Series, Entries((A, 12)), currentSharedRevision: 2).Warnings, Is.Empty);
    }

    [Test]
    public void W4WarnsWhenTheSharedMatchAppearedOrWasDeleted() {
        var withoutBase = new Match(new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid()), [Range(1, 12, A)], []);
        var withBase = withoutBase with { BaseSharedRevision = 2 };

        Assert.That(MatchValidator.Validate(withoutBase, MediaKind.Series, Entries((A, 12)), currentSharedRevision: 1).Warnings.Single().Rule, Is.EqualTo("W4"));
        Assert.That(MatchValidator.Validate(withBase, MediaKind.Series, Entries((A, 12))).Warnings.Single().Rule, Is.EqualTo("W4"));
    }

    [Test]
    public void W4DoesNotApplyToASharedMatch() =>
        Assert.That(MatchValidator.Validate(Shared(Range(1, 12, A)), MediaKind.Series, Entries((A, 12)), currentSharedRevision: 3).Warnings, Is.Empty);
}
