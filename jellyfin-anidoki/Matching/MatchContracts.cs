using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

// Requests and responses of the match API. Jellyfin writes PascalCase by default, so every name is given. The match
// model's own records stay unannotated: their JSON is what the store has already written.

/// <summary>Simkl's cross IDs, which it needs to read an entry again.</summary>
public sealed record LookupContract(
    [property: JsonPropertyName("aniList")] int? AniList,
    [property: JsonPropertyName("mal")] int? Mal,
    [property: JsonPropertyName("kitsu")] int? Kitsu,
    [property: JsonPropertyName("aniDb")] int? AniDb,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] MediaKind Kind) {
    public static LookupContract? From(CrossReference? lookup) =>
        lookup == null ? null : new(lookup.Ids.AniList, lookup.Ids.Mal, lookup.Ids.Kitsu, lookup.Ids.AniDb, lookup.Title, lookup.Kind);

    public CrossReference ToLookup() => new(new ResolvedIds(AniList, Mal, Kitsu, AniDb), Title, Kind);
}

/// <summary>A tracker entry. The provider is the route's.</summary>
public sealed record EntryContract(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("alternativeId")] string? AlternativeId,
    [property: JsonPropertyName("lookup")] LookupContract? Lookup,
    [property: JsonPropertyName("title")] string Title) {
    public static EntryContract From(MatchTarget target) => From(target.Entry, target.Title);

    public static EntryContract From(EntryRef entry, string title) => new(entry.Id, entry.AlternativeId, LookupContract.From(entry.Lookup), title);

    public MatchTarget ToTarget(ApiName provider) => new(new EntryRef(provider, Id, AlternativeId, Lookup?.ToLookup()), Title);
}

public sealed record RangeContract(
    [property: JsonPropertyName("sourceStart")] int SourceStart,
    [property: JsonPropertyName("sourceEnd")] int? SourceEnd,
    [property: JsonPropertyName("target")] EntryContract? Target,
    [property: JsonPropertyName("targetStart")] int TargetStart);

/// <param name="Kind"><c>target</c> or <c>ignore</c>.</param>
public sealed record ExceptionContract(
    [property: JsonPropertyName("sourceEpisode")] int SourceEpisode,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("target")] EntryContract? Target,
    [property: JsonPropertyName("targetEpisode")] int? TargetEpisode);

/// <summary>The ranges and exceptions of a match, a draft or a revision.</summary>
public sealed record MappingContract(
    [property: JsonPropertyName("ranges")] IReadOnlyList<RangeContract> Ranges,
    [property: JsonPropertyName("exceptions")] IReadOnlyList<ExceptionContract> Exceptions) {
    public static MappingContract From(IReadOnlyList<MatchRange> ranges, IReadOnlyList<MatchException> exceptions) => new(
        ranges.Select(range => new RangeContract(range.SourceStart, range.SourceEnd, EntryContract.From(range.Target), range.TargetStart)).ToList(),
        exceptions.Select(exception => exception switch {
            TargetException target => new ExceptionContract(target.SourceEpisode, "target", EntryContract.From(target.Target), target.TargetEpisode),
            _ => new ExceptionContract(exception.SourceEpisode, "ignore", null, null)
        }).ToList());
}

/// <param name="ExpectedRevision">The revision the editor last saw; 0 when none existed.</param>
public sealed record SaveMatchRequest(
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision,
    [property: JsonPropertyName("ranges")] IReadOnlyList<RangeContract>? Ranges,
    [property: JsonPropertyName("exceptions")] IReadOnlyList<ExceptionContract>? Exceptions,
    [property: JsonPropertyName("note")] string? Note);

public sealed record PreviewRequest(
    [property: JsonPropertyName("ranges")] IReadOnlyList<RangeContract>? Ranges,
    [property: JsonPropertyName("exceptions")] IReadOnlyList<ExceptionContract>? Exceptions);

public sealed record PromoteRequest(
    [property: JsonPropertyName("expectedSharedRevision")] long ExpectedSharedRevision,
    [property: JsonPropertyName("expectedOverrideRevision")] long ExpectedOverrideRevision,
    [property: JsonPropertyName("removeOverride")] bool RemoveOverride = true);

public sealed record IssueContract(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("message")] string Message) {
    public static IReadOnlyList<IssueContract> From(IEnumerable<MatchIssue> issues) => issues.Select(issue => new IssueContract(issue.Rule, issue.Message)).ToList();
}

public sealed record SavedContract(
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("warnings")] IReadOnlyList<IssueContract> Warnings);

/// <summary>The body of a <c>422</c>: the save was refused, and nothing was written.</summary>
public sealed record InvalidMatchContract(
    [property: JsonPropertyName("errors")] IReadOnlyList<IssueContract> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<IssueContract> Warnings);

/// <summary>The body of a <c>409</c>. 0 means none exists now.</summary>
public sealed record RevisionConflictContract([property: JsonPropertyName("currentRevision")] long CurrentRevision);

public sealed record PromotionConflictContract(
    [property: JsonPropertyName("sharedRevision")] long SharedRevision,
    [property: JsonPropertyName("overrideRevision")] long OverrideRevision);

/// <param name="DifferingOverrides">Other users' overrides of this source and provider that differ from the new shared match. A count, never names.</param>
public sealed record PromotedContract(
    [property: JsonPropertyName("sharedRevision")] long SharedRevision,
    [property: JsonPropertyName("overrideRevision")] long OverrideRevision,
    [property: JsonPropertyName("differingOverrides")] int DifferingOverrides,
    [property: JsonPropertyName("warnings")] IReadOnlyList<IssueContract> Warnings);

/// <param name="Stale">Personal only: the shared match has moved on since this override was saved (W4).</param>
public sealed record MatchView(
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("baseSharedRevision")] long? BaseSharedRevision,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("updatedBy")] string UpdatedBy,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("mapping")] MappingContract Mapping);

public sealed record HistoryContract(
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("action")] MatchRevisionAction Action,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("mapping")] MappingContract Mapping,
    [property: JsonPropertyName("promotedFrom")] string? PromotedFrom);

public sealed record OverrideContract(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("baseSharedRevision")] long? BaseSharedRevision,
    [property: JsonPropertyName("stale")] bool Stale);

/// <summary>One provider's saved state in the source list. Providers with nothing saved are left out and resolve automatically.</summary>
/// <param name="Corrections">Admin only: how many users have an override.</param>
/// <param name="PersonalRevision">User only: their own override.</param>
public sealed record ProviderStateContract(
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("sharedRevision")] long? SharedRevision,
    [property: JsonPropertyName("corrections")] int? Corrections,
    [property: JsonPropertyName("personalRevision")] long? PersonalRevision);

public sealed record SourceSummaryContract(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("kind")] MatchSourceKind Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("providers")] IReadOnlyList<ProviderStateContract> Providers);

/// <param name="Page">1-based.</param>
public sealed record SourcePageContract(
    [property: JsonPropertyName("items")] IReadOnlyList<SourceSummaryContract> Items,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize);

public sealed record ProviderDetailContract(
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("shared")] MatchView? Shared,
    [property: JsonPropertyName("history")] IReadOnlyList<HistoryContract> History,
    [property: JsonPropertyName("overrides")] IReadOnlyList<OverrideContract> Overrides);

/// <param name="Episodes">The episode numbers the library holds.</param>
public sealed record SourceDetailContract(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("kind")] MatchSourceKind Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("episodes")] IReadOnlyList<int> Episodes,
    [property: JsonPropertyName("providers")] IReadOnlyList<ProviderDetailContract> Providers);

/// <param name="Effective">Personal, Shared or Automatic: what sync uses for this user once saved matches apply.</param>
public sealed record UserProviderContract(
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("effective")] string Effective,
    [property: JsonPropertyName("shared")] MatchView? Shared,
    [property: JsonPropertyName("personal")] MatchView? Personal);

public sealed record UserSourceDetailContract(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("kind")] MatchSourceKind Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("episodes")] IReadOnlyList<int> Episodes,
    [property: JsonPropertyName("providers")] IReadOnlyList<UserProviderContract> Providers);

/// <param name="Outcome"><c>mapped</c>, <c>ignored</c>, or the reason sync would record: <c>not-in-match</c>, <c>match-target-unreadable</c>, <c>past-entry-end</c>.</param>
/// <param name="Target">Where the match sends the episode, even when it would not sync.</param>
public sealed record PreviewRowContract(
    [property: JsonPropertyName("sourceEpisode")] int SourceEpisode,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("target")] EntryContract? Target,
    [property: JsonPropertyName("targetEpisode")] int? TargetEpisode);

/// <param name="Progress">Highest-watched progress (D5).</param>
/// <param name="ListStatus">The entry on the user's list now; null when it is not on it.</param>
public sealed record PreviewProgressContract(
    [property: JsonPropertyName("target")] EntryContract Target,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("listStatus")] TrackerStatus? ListStatus,
    [property: JsonPropertyName("listProgress")] int? ListProgress);

/// <param name="Progress">Null when no user was asked for, or that user has not linked the provider.</param>
/// <param name="BorrowedLink">Titles and totals were read through another user's link (D12).</param>
public sealed record PreviewContract(
    [property: JsonPropertyName("rows")] IReadOnlyList<PreviewRowContract> Rows,
    [property: JsonPropertyName("errors")] IReadOnlyList<IssueContract> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<IssueContract> Warnings,
    [property: JsonPropertyName("progress")] IReadOnlyList<PreviewProgressContract>? Progress,
    [property: JsonPropertyName("borrowedLink")] bool BorrowedLink);

public sealed record EntryCandidateContract(
    [property: JsonPropertyName("entry")] EntryContract Entry,
    [property: JsonPropertyName("english")] string? English,
    [property: JsonPropertyName("japanese")] string? Japanese,
    [property: JsonPropertyName("totalEpisodes")] int TotalEpisodes) {
    public static EntryCandidateContract From(EntryRef entry, EntryTitles? titles, int totalEpisodes) =>
        new(EntryContract.From(entry, titles?.Main ?? titles?.English ?? titles?.Japanese ?? ""), titles?.English, titles?.Japanese, totalEpisodes);
}

public sealed record EntrySearchContract(
    [property: JsonPropertyName("entries")] IReadOnlyList<EntryCandidateContract> Entries,
    [property: JsonPropertyName("borrowedLink")] bool BorrowedLink);

/// <param name="Mapping">What automatic resolution would save; null when no match can reproduce it (PR 9, Differences item 7).</param>
/// <param name="Reason">Why there is none.</param>
public sealed record AutomaticContract(
    [property: JsonPropertyName("mapping")] MappingContract? Mapping,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("borrowedLink")] bool BorrowedLink);

internal static class MatchContracts {
    /// <returns>The match, or what is wrong with the request's shape. Rules about the numbers are <see cref="MatchValidator"/>'s.</returns>
    public static (Match? Match, string? Error) ToMatch(MatchScope scope, IReadOnlyList<RangeContract>? ranges, IReadOnlyList<ExceptionContract>? exceptions,
        long? baseSharedRevision) {
        var matchRanges = new List<MatchRange>();
        foreach (RangeContract range in ranges ?? []) {
            if (Missing(range.Target) is { } error)
                return (null, $"Range starting at episode {range.SourceStart}: {error}");
            matchRanges.Add(new MatchRange(range.SourceStart, range.SourceEnd, range.Target!.ToTarget(scope.Provider), range.TargetStart));
        }

        var matchExceptions = new List<MatchException>();
        foreach (ExceptionContract exception in exceptions ?? []) {
            switch (exception.Kind) {
                case "ignore":
                    matchExceptions.Add(new IgnoreException(exception.SourceEpisode));
                    break;
                case "target" when Missing(exception.Target) is { } error:
                    return (null, $"Exception for episode {exception.SourceEpisode}: {error}");
                case "target" when exception.TargetEpisode is int episode:
                    matchExceptions.Add(new TargetException(exception.SourceEpisode, exception.Target!.ToTarget(scope.Provider), episode));
                    break;
                case "target":
                    return (null, $"Exception for episode {exception.SourceEpisode}: choose the entry's episode.");
                default:
                    return (null, $"Exception for episode {exception.SourceEpisode}: the kind must be \"target\" or \"ignore\".");
            }
        }
        if (matchExceptions.GroupBy(exception => exception.SourceEpisode).FirstOrDefault(group => group.Count() > 1) is { } repeated)
            return (null, $"Episode {repeated.Key} has more than one exception.");
        return (new Match(scope, matchRanges, matchExceptions, 0, scope.Owner == null ? null : baseSharedRevision), null);
    }

    private static string? Missing(EntryContract? target) => target switch {
        null => "choose an entry.",
        { Title: null or "" } => "the entry needs a title.",
        _ => null
    };
}
