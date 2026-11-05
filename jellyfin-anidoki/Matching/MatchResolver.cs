using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

/// <summary>Which watched episodes count towards a matched season's progress.</summary>
public enum MatchProgressScope {
    /// <summary>A played episode: the entry holding it and those it is past.</summary>
    PlayedEpisode,

    /// <summary>A manual sync, which covers every entry of the match.</summary>
    WholeSeason
}

/// <summary>
/// Adds saved matches to automatic resolution. A season with no match resolves exactly as before, with no extra reads.
/// With <c>applySavedMatches</c> off, the automatic answer drives the plan and the saved one is only noted; nothing about
/// a saved match can then affect a write. With it on, a saved match is authoritative (D4): it never falls back.
/// </summary>
public sealed class MatchResolver(ISourceResolver automatic, MatchStore matches, Guid userId, bool applySavedMatches,
    ISeasonWatchState? watchState, MatchProgressScope scope, ILogger logger) : ISourceResolver {
    public async Task<SourceResolution> Resolve(SyncSource source, SourceIds ids, ITrackerProvider tracker, CancellationToken cancellationToken) {
        if (applySavedMatches) {
            // The provider is linked, or the pipeline would not be asking for it. A store failure propagates: writing to
            // the entry someone corrected away from would be worse than writing nothing.
            Match? match = matches.GetEffective(userId, tracker.Name, source.SourceKey, true);
            return match == null
                ? await automatic.Resolve(source, ids, tracker, cancellationToken)
                : await ThroughMatch(match, source, tracker, cancellationToken);
        }

        SourceResolution answer = await automatic.Resolve(source, ids, tracker, cancellationToken);
        string? note = await ShadowNote(source, tracker, cancellationToken);
        if (note == null)
            return answer;
        logger.LogDebug("({ApiName}) {SavedMatchNote}", tracker.Name, note);
        return answer switch {
            Resolved resolved => resolved with { Note = Join(resolved.Note, note) },
            Unresolved unresolved => unresolved with { Note = Join(unresolved.Note ?? unresolved.Reason, note) },
            _ => answer
        };
    }

    /// <summary>Reads each target the match points at. One that cannot be read is simply missing.</summary>
    internal static async Task<Dictionary<EntryRef, EntryState>> ReadTargets(Match match, ITrackerProvider tracker, CancellationToken cancellationToken) {
        var entries = new Dictionary<EntryRef, EntryState>();
        foreach (MatchTarget target in match.Targets) {
            if (await tracker.ReadEntry(target.Entry, cancellationToken) is { } state)
                entries[target.Entry] = state;
        }
        return entries;
    }

    private async Task<SourceResolution> ThroughMatch(Match match, SyncSource source, ITrackerProvider tracker, CancellationToken cancellationToken) {
        Dictionary<EntryRef, EntryState> entries = await ReadTargets(match, tracker, cancellationToken);
        SourceResolution answer = RangeResolver.Resolve(match, source.EpisodeNumber, source.SearchTitle, entries);
        if (answer is not Resolved resolved)
            return answer;
        IEnumerable<int> watched = (watchState?.Watched(source, userId) ?? []).Select(episode => episode.Number);
        IReadOnlyList<EntryProgress> progress = scope == MatchProgressScope.WholeSeason
            ? ProgressAggregator.ForWholeSeason(match, watched.Append(source.EpisodeNumber), entries)
            : ProgressAggregator.ForPlayback(match, source.EpisodeNumber, watched, entries);
        return WithProgress(resolved, progress, entries);
    }

    /// <summary>
    /// D5: a matched season's numbers come from what was watched, not from the episode played. An earlier entry with
    /// nothing watched therefore gets no target, unlike the automatic walk, which completes it.
    /// An entry that is not behind keeps the played episode's number, or stays untouched if it is an earlier part.
    /// </summary>
    private static Resolved WithProgress(Resolved resolved, IReadOnlyList<EntryProgress> progress, Dictionary<EntryRef, EntryState> entries) {
        ResolvedTarget played = resolved.Played;
        var earlier = new List<ResolvedTarget>();
        foreach (EntryProgress entry in progress) {
            EntryState? state = entries.GetValueOrDefault(entry.Target.Entry);
            if (!IsBehind(state))
                continue;
            if (entry.Target.Entry == played.Entry)
                played = played with { TargetEpisode = entry.Progress };
            else
                earlier.Add(new ResolvedTarget(entry.Target.Entry, entry.Progress, TargetRole.EarlierPart, entry.Target.Title, state, played.MatchRevision));
        }
        return resolved with { Played = played, EarlierParts = earlier };
    }

    /// <summary>
    /// Finished or being rewatched, an entry is not behind: the planner reads the number as a place in the rewatch, so the
    /// highest watched would finish it, counting a rewatch for every episode played.
    /// </summary>
    internal static bool IsBehind(EntryState? state) => state?.List is not ({ Status: TrackerStatus.Completed } or { Rewatching: true });

    /// <returns>What a saved match would do for this source, or null when there is none. Nothing here may affect the sync.</returns>
    private async Task<string?> ShadowNote(SyncSource source, ITrackerProvider tracker, CancellationToken cancellationToken) {
        try {
            Match? match = matches.GetEffective(userId, tracker.Name, source.SourceKey, true);
            if (match == null)
                return null;
            return await ThroughMatch(match, source, tracker, cancellationToken) switch {
                Resolved { Played: var played } => $"saved match would resolve to {played.Title} ep {played.TargetEpisode}",
                Ignored => "saved match would ignore this episode",
                Unresolved { Reason: var reason } => $"saved match would not sync this episode: {reason}",
                _ => null
            };
        } catch (Exception error) when (error is TrackerFailureException or SqliteException or InvalidOperationException) {
            logger.LogDebug("({ApiName}) Could not check the saved match: {ErrorMessage}", tracker.Name, error.Message);
            return "saved match could not be checked";
        }
    }

    private static string Join(string? first, string second) => string.IsNullOrEmpty(first) ? second : $"{first}; {second}";
}
