using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

/// <summary>Where a match sends one source episode, before anything is known about the entry.</summary>
public abstract record EpisodeMapping;

public sealed record MappedEpisode(MatchTarget Target, int TargetEpisode) : EpisodeMapping;

public sealed record IgnoredEpisode : EpisodeMapping;

public sealed record UnmappedEpisode : EpisodeMapping;

/// <summary>
/// Resolves a source episode through a match. Pure: the caller reads the entries first, and an entry missing from
/// <c>entries</c> could not be read. A saved match is authoritative, so nothing here falls back to another answer.
/// </summary>
public static class RangeResolver {
    private static readonly IReadOnlyList<ResolvedTarget> None = [];

    /// <summary>An exception wins over any range holding the episode.</summary>
    public static EpisodeMapping Map(Match match, int sourceEpisode) {
        switch (match.Exceptions.FirstOrDefault(exception => exception.SourceEpisode == sourceEpisode)) {
            case IgnoreException:
                return new IgnoredEpisode();
            case TargetException target:
                return new MappedEpisode(target.Target, target.TargetEpisode);
        }

        MatchRange? range = match.Ranges.FirstOrDefault(candidate => candidate.Contains(sourceEpisode));
        return range == null ? new UnmappedEpisode() : new MappedEpisode(range.Target, range.TargetFor(sourceEpisode));
    }

    /// <param name="sourceTitle">Recorded when the episode is not in the match, which names no entry to record.</param>
    /// <param name="entries">The match's targets as read. Only the played target has to be present.</param>
    public static SourceResolution Resolve(Match match, int sourceEpisode, string sourceTitle, IReadOnlyDictionary<EntryRef, EntryState> entries) {
        switch (Map(match, sourceEpisode)) {
            case IgnoredEpisode:
                return new Ignored(match.By, match.RecordedRevision);
            case MappedEpisode { Target: var target, TargetEpisode: var episode }:
                if (!entries.TryGetValue(target.Entry, out EntryState? state))
                    return new Unresolved("match-target-unreadable", Record(target), None);
                if (IsPastEnd(state, episode))
                    return new Unresolved("past-entry-end", Record(target), None);
                var played = new ResolvedTarget(target.Entry, episode, TargetRole.Played, target.Title, state, match.RecordedRevision);
                return new Resolved(played, match.By, EarlierParts(match, sourceEpisode, target.Entry, entries));
            default:
                return new Unresolved("not-in-match", new UnresolvedRecord("", sourceTitle), None);
        }
    }

    /// <summary>The ranges the episode is past, one per entry in source order, not counting the entry it lands on.</summary>
    internal static IEnumerable<(MatchTarget Target, int LastEpisode)> EarlierRanges(Match match, int sourceEpisode, EntryRef played) =>
        match.Ranges
            .Where(range => range.SourceEnd < sourceEpisode && range.Target.Entry != played)
            .OrderBy(range => range.SourceStart)
            .GroupBy(range => range.Target.Entry)
            .Select(group => (group.First().Target, group.Max(range => range.TargetEnd!.Value)));

    /// <summary>An entry of unknown length (0) has no end to be past.</summary>
    internal static bool IsPastEnd(EntryState state, int episode) => state.TotalEpisodes > 0 && episode > state.TotalEpisodes;

    private static List<ResolvedTarget> EarlierParts(Match match, int sourceEpisode, EntryRef played, IReadOnlyDictionary<EntryRef, EntryState> entries) =>
        EarlierRanges(match, sourceEpisode, played).Select(range => {
            // A part that cannot be read here is read again by the executor, which records it alone as unreadable.
            EntryState? state = entries.GetValueOrDefault(range.Target.Entry);
            // A range saved while the length was unknown can run past it now; complete the entry at its real length.
            int episode = state is { TotalEpisodes: > 0 } ? Math.Min(range.LastEpisode, state.TotalEpisodes) : range.LastEpisode;
            return new ResolvedTarget(range.Target.Entry, episode, TargetRole.EarlierPart, range.Target.Title, state, match.RecordedRevision);
        }).ToList();

    private static UnresolvedRecord Record(MatchTarget target) =>
        new(target.Entry.AlternativeId ?? (target.Entry.Lookup == null ? target.Entry.Id.ToString() : ""), target.Title);
}
