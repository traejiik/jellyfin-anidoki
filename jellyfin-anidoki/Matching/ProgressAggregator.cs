using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

/// <summary>How far one entry should be, from what was watched.</summary>
public sealed record EntryProgress(MatchTarget Target, int Progress);

/// <summary>
/// Highest-watched progress for a matched season: each entry's progress is the highest entry episode among the season's
/// watched episodes mapped to it. Pure. Unmatched seasons keep played-episode progress.
/// </summary>
public static class ProgressAggregator {
    /// <summary>
    /// A played episode touches the entry holding it and each entry with a range it is past. Later entries stay as they
    /// are. The played episode counts as watched even before Jellyfin sets its played flag. A touched entry with nothing
    /// watched has no progress to report.
    /// </summary>
    public static IReadOnlyList<EntryProgress> ForPlayback(Match match, int playedEpisode, IEnumerable<int> watchedEpisodes,
        IReadOnlyDictionary<EntryRef, EntryState> entries) {
        if (RangeResolver.Map(match, playedEpisode) is not MappedEpisode played)
            return [];
        var touched = RangeResolver.EarlierRanges(match, playedEpisode, played.Target.Entry)
            .Select(range => range.Target.Entry)
            .Append(played.Target.Entry)
            .ToHashSet();
        return Highest(match, watchedEpisodes.Append(playedEpisode), entries)
            .Where(progress => touched.Contains(progress.Target.Entry))
            .ToList();
    }

    /// <summary>A manual sync covers the whole season, so every entry with something watched is reported.</summary>
    public static IReadOnlyList<EntryProgress> ForWholeSeason(Match match, IEnumerable<int> watchedEpisodes, IReadOnlyDictionary<EntryRef, EntryState> entries) =>
        Highest(match, watchedEpisodes, entries);

    /// <summary>In source order. An episode past its entry's known end is not progress on that entry.</summary>
    private static List<EntryProgress> Highest(Match match, IEnumerable<int> watchedEpisodes, IReadOnlyDictionary<EntryRef, EntryState> entries) {
        var highest = new List<EntryProgress>();
        foreach (int episode in watchedEpisodes.Distinct().Order()) {
            if (RangeResolver.Map(match, episode) is not MappedEpisode mapped)
                continue;
            if (entries.TryGetValue(mapped.Target.Entry, out EntryState? state) && RangeResolver.IsPastEnd(state, mapped.TargetEpisode))
                continue;
            int index = highest.FindIndex(progress => progress.Target.Entry == mapped.Target.Entry);
            if (index < 0)
                highest.Add(new EntryProgress(mapped.Target, mapped.TargetEpisode));
            else if (mapped.TargetEpisode > highest[index].Progress)
                highest[index] = highest[index] with { Progress = mapped.TargetEpisode };
        }

        return highest;
    }
}
