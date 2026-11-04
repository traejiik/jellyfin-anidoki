using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

/// <param name="Rule">The rule's code, V1–V8 or W1–W4.</param>
/// <param name="Message">What is wrong, in plain words, for the editor and the API's error list.</param>
public sealed record MatchIssue(string Rule, string Message);

public sealed record MatchValidation(IReadOnlyList<MatchIssue> Errors, IReadOnlyList<MatchIssue> Warnings) {
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The rules a match must meet to be saved. Errors block the save; warnings are shown and allowed. Pure: the caller reads
/// the match's targets first, and a target missing from <c>entries</c> could not be read.
/// </summary>
public static class MatchValidator {
    public const int MaxRanges = 50;
    public const int MaxExceptions = 500;

    /// <summary>Far past any real episode number, and low enough that the range arithmetic cannot overflow.</summary>
    public const int MaxEpisodeNumber = 1_000_000;

    /// <param name="kind">Whether the source is a season or a movie.</param>
    /// <param name="currentSharedRevision">Personal only: the shared match's revision now, or null when none exists.</param>
    public static MatchValidation Validate(Match match, MediaKind kind, IReadOnlyDictionary<EntryRef, EntryState> entries, long? currentSharedRevision = null) {
        var errors = new List<MatchIssue>();
        CheckShape(match, errors);
        // Everything after this relies on the shape: sorted, bounded, and with only the last range open.
        if (errors.Count > 0)
            return new MatchValidation(errors, []);

        List<MatchRange> sorted = match.Ranges.OrderBy(range => range.SourceStart).ToList();
        CheckOverlaps(match, sorted, errors);
        CheckTargets(match, entries, errors);
        CheckAmbiguity(match, errors);
        if (kind == MediaKind.Movie)
            CheckMovie(match, errors);

        var warnings = new List<MatchIssue>();
        WarnGaps(match, sorted, warnings);
        WarnUnknownTotals(match, entries, warnings);
        WarnSkippedTargets(match, sorted, warnings);
        if (match.Scope.Owner != null && match.BaseSharedRevision != currentSharedRevision)
            warnings.Add(new MatchIssue("W4", "The shared match changed after this correction was saved."));
        return new MatchValidation(errors, warnings);
    }

    /// <summary>V1, V2 and V8.</summary>
    private static void CheckShape(Match match, List<MatchIssue> errors) {
        if (match.Ranges.Count == 0 && match.Exceptions.Count == 0)
            errors.Add(new MatchIssue("V1", "A match needs at least one range or exception."));
        if (match.Ranges.Count > MaxRanges)
            errors.Add(new MatchIssue("V8", $"A match can have at most {MaxRanges} ranges."));
        if (match.Exceptions.Count > MaxExceptions)
            errors.Add(new MatchIssue("V8", $"A match can have at most {MaxExceptions} exceptions."));
        for (int index = 0; index < match.Ranges.Count; index++) {
            MatchRange range = match.Ranges[index];
            if (range.SourceStart < 0 || range.SourceEnd < range.SourceStart)
                errors.Add(new MatchIssue("V2", $"Range {index + 1} must run forwards from episode 0 or later."));
            if (range.SourceEnd == null && index != match.Ranges.Count - 1)
                errors.Add(new MatchIssue("V2", $"Range {index + 1} is open, but only the last range can be."));
            if (range.SourceStart > MaxEpisodeNumber || range.SourceEnd > MaxEpisodeNumber || range.TargetStart > MaxEpisodeNumber)
                errors.Add(new MatchIssue("V2", $"Range {index + 1} has an episode number above {MaxEpisodeNumber}."));
        }

        foreach (MatchException exception in match.Exceptions) {
            if (exception.SourceEpisode < 0 || exception.SourceEpisode > MaxEpisodeNumber)
                errors.Add(new MatchIssue("V2", $"Exception episode {exception.SourceEpisode} is out of range."));
            if (exception is TargetException { TargetEpisode: > MaxEpisodeNumber })
                errors.Add(new MatchIssue("V2", $"The exception for episode {exception.SourceEpisode} has an episode number above {MaxEpisodeNumber}."));
        }
    }

    /// <summary>V3: an episode has one answer, so neither two ranges nor two exceptions may claim it.</summary>
    private static void CheckOverlaps(Match match, List<MatchRange> sorted, List<MatchIssue> errors) {
        foreach (var (first, second) in sorted.Zip(sorted.Skip(1))) {
            if (first.SourceEnd == null || second.SourceStart <= first.SourceEnd)
                errors.Add(new MatchIssue("V3", $"The ranges starting at episodes {first.SourceStart} and {second.SourceStart} overlap."));
        }

        foreach (var repeated in match.Exceptions.GroupBy(exception => exception.SourceEpisode).Where(group => group.Count() > 1))
            errors.Add(new MatchIssue("V3", $"Episode {repeated.Key} has more than one exception."));
    }

    /// <summary>V4 and V6.</summary>
    private static void CheckTargets(Match match, IReadOnlyDictionary<EntryRef, EntryState> entries, List<MatchIssue> errors) {
        foreach (MatchTarget target in match.Targets) {
            if (!entries.ContainsKey(target.Entry))
                errors.Add(new MatchIssue("V6", $"\"{target.Title}\" could not be read from the tracker."));
        }

        foreach (MatchRange range in match.Ranges) {
            if (range.TargetStart < 1)
                errors.Add(new MatchIssue("V4", $"The range starting at episode {range.SourceStart} must target episode 1 or later."));
            // An open range has no end to check, but it must at least start inside the entry.
            if (Total(entries, range.Target) is int total && (range.TargetEnd ?? range.TargetStart) > total)
                errors.Add(new MatchIssue("V4", $"The range starting at episode {range.SourceStart} runs past the {total} episodes of \"{range.Target.Title}\"."));
        }

        foreach (TargetException exception in match.Exceptions.OfType<TargetException>()) {
            if (exception.TargetEpisode < 1)
                errors.Add(new MatchIssue("V4", $"The exception for episode {exception.SourceEpisode} must target episode 1 or later."));
            if (Total(entries, exception.Target) is int total && exception.TargetEpisode > total)
                errors.Add(new MatchIssue("V4", $"The exception for episode {exception.SourceEpisode} is past the {total} episodes of \"{exception.Target.Title}\"."));
        }
    }

    /// <summary>
    /// V5: no two source episodes may land on one entry episode. An episode with an exception has left its range, so
    /// each loop below only continues past such an episode, which V8 bounds, however wide the ranges are.
    /// </summary>
    private static void CheckAmbiguity(Match match, List<MatchIssue> errors) {
        var excepted = match.Exceptions.Select(exception => exception.SourceEpisode).ToHashSet();
        List<TargetException> targets = match.Exceptions.OfType<TargetException>().ToList();
        foreach (var shared in targets.GroupBy(exception => (exception.Target.Entry, exception.TargetEpisode)).Where(group => group.Count() > 1))
            AddAmbiguity(errors, shared.First().Target, shared.Key.TargetEpisode);

        foreach (TargetException exception in targets) {
            foreach (MatchRange range in match.Ranges.Where(range => range.Target.Entry == exception.Target.Entry)) {
                int source = range.SourceStart + (exception.TargetEpisode - range.TargetStart);
                if (range.Contains(source) && !excepted.Contains(source))
                    AddAmbiguity(errors, exception.Target, exception.TargetEpisode);
            }
        }

        for (int outer = 0; outer < match.Ranges.Count; outer++) {
            for (int inner = outer + 1; inner < match.Ranges.Count; inner++) {
                MatchRange first = match.Ranges[outer];
                MatchRange second = match.Ranges[inner];
                if (first.Target.Entry == second.Target.Entry && SharedTarget(first, second, excepted) is int episode)
                    AddAmbiguity(errors, first.Target, episode);
            }
        }
    }

    /// <returns>An entry episode both ranges reach from a source episode still in the range, or null.</returns>
    private static int? SharedTarget(MatchRange first, MatchRange second, HashSet<int> excepted) {
        int low = Math.Max(first.TargetStart, second.TargetStart);
        int high = Math.Min(first.TargetEnd ?? int.MaxValue, second.TargetEnd ?? int.MaxValue);
        for (int episode = low; episode <= high; episode++) {
            int fromFirst = first.SourceStart + (episode - first.TargetStart);
            int fromSecond = second.SourceStart + (episode - second.TargetStart);
            if (!excepted.Contains(fromFirst) && !excepted.Contains(fromSecond) && fromFirst != fromSecond)
                return episode;
        }

        return null;
    }

    private static void AddAmbiguity(List<MatchIssue> errors, MatchTarget target, int episode) =>
        errors.Add(new MatchIssue("V5", $"More than one episode goes to episode {episode} of \"{target.Title}\"."));

    /// <summary>V7: a movie is episode 1 of its own source.</summary>
    private static void CheckMovie(Match match, List<MatchIssue> errors) {
        bool singleRange = match.Exceptions.Count == 0 && match.Ranges is [{ SourceStart: 1, SourceEnd: 1 }];
        bool singleException = match.Ranges.Count == 0 && match.Exceptions is [{ SourceEpisode: 1 }];
        if (!singleRange && !singleException)
            errors.Add(new MatchIssue("V7", "A movie takes one range from episode 1 to 1, or one exception for episode 1."));
    }

    /// <summary>W1. A gap every episode of which has an exception leaves nothing unmatched.</summary>
    private static void WarnGaps(Match match, List<MatchRange> sorted, List<MatchIssue> warnings) {
        foreach (var (first, second) in sorted.Zip(sorted.Skip(1))) {
            if (first.SourceEnd is not int end || second.SourceStart <= end + 1)
                continue;
            int gap = second.SourceStart - end - 1;
            int covered = match.Exceptions.Select(exception => exception.SourceEpisode).Distinct().Count(episode => episode > end && episode < second.SourceStart);
            if (covered < gap)
                warnings.Add(new MatchIssue("W1", $"Episodes {end + 1} to {second.SourceStart - 1} are not in this match and will not sync."));
        }
    }

    /// <summary>W2.</summary>
    private static void WarnUnknownTotals(Match match, IReadOnlyDictionary<EntryRef, EntryState> entries, List<MatchIssue> warnings) {
        foreach (MatchTarget target in match.Targets) {
            if (entries.TryGetValue(target.Entry, out EntryState? state) && state.TotalEpisodes == 0)
                warnings.Add(new MatchIssue("W2", $"\"{target.Title}\" has no known length yet; it is checked again at each sync."));
        }
    }

    /// <summary>W3: the first range starts late, or an entry's mapped episodes skip some. Ranges only: a recap ignored mid-range is deliberate.</summary>
    private static void WarnSkippedTargets(Match match, List<MatchRange> sorted, List<MatchIssue> warnings) {
        if (sorted.Count > 0 && sorted[0].SourceStart > 1)
            warnings.Add(new MatchIssue("W3", $"The first range starts at episode {sorted[0].SourceStart}, not episode 1."));
        foreach (var group in match.Ranges.GroupBy(range => range.Target.Entry)) {
            int next = 1;
            foreach (MatchRange range in group.OrderBy(range => range.TargetStart)) {
                if (range.TargetStart > next)
                    warnings.Add(new MatchIssue("W3", $"Episodes {next} to {range.TargetStart - 1} of \"{range.Target.Title}\" are skipped."));
                if (range.TargetEnd is not int end)
                    break;
                next = Math.Max(next, end + 1);
            }
        }
    }

    private static int? Total(IReadOnlyDictionary<EntryRef, EntryState> entries, MatchTarget target) =>
        entries.TryGetValue(target.Entry, out EntryState? state) && state.TotalEpisodes > 0 ? state.TotalEpisodes : null;
}
