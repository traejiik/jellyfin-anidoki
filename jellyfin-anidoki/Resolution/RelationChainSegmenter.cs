using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Resolution;

/// <summary>An entry in a relation chain: what the segmenter needs from a read entry or a search candidate.</summary>
/// <param name="State">The read entry; null for a search candidate, which was never read.</param>
public sealed record ChainLink(EntryRef Entry, int TotalEpisodes, EntryTitles? Titles, EntryState? State = null) {
    public static ChainLink Of(EntryState state) => new(state.Entry, state.TotalEpisodes, state.Titles, state);

    public static ChainLink Of(EntryCandidate candidate) => new(candidate.Entry, candidate.TotalEpisodes, candidate.Titles);
}

/// <param name="Target">The cour holding the episode, or null when none can.</param>
/// <param name="EpisodesBefore">Episodes in the cours before the target.</param>
/// <param name="FailureReason">sequel-fetch-failed or no-cour-covers-episode when <paramref name="Target"/> is null.</param>
public sealed record ChainWalk(ChainLink? Target, int EpisodesBefore, IReadOnlyList<ResolvedTarget> EarlierParts, string? FailureReason);

/// <summary>An automatic answer rendered as an unsaved match.</summary>
/// <param name="Match">Null when no match can reproduce the automatic answer, so the season stays automatic.</param>
/// <param name="FailureReason">Why <paramref name="Match"/> is null: the automatic reason, not-found, past-entry-end, or offset-not-representable.</param>
public sealed record DerivedSegmentation(Match? Match, string? FailureReason);

/// <summary>
/// Follows a tracker's sequel relations, where Jellyfin numbers one season and the tracker splits it into entries.
/// Reads only.
/// </summary>
public sealed class RelationChainSegmenter(ITrackerProvider tracker, ILogger logger) {
    /// <returns>The sequel's state, or null when the entry's relations or the sequel cannot be read, or there is no sequel.</returns>
    public async Task<EntryState?> FollowSequel(EntryRef entry, CancellationToken cancellationToken) =>
        await FollowSequels(entry, 1, cancellationToken);

    /// <summary>
    /// Get further seasons. Jellyfin numbers seasons, whereas trackers make each one its own entry, so season n of a
    /// title-search match is n - 1 sequel hops away.
    /// </summary>
    /// <returns>The season reached, or null when a relation or the season cannot be read, or there is no sequel.</returns>
    public async Task<EntryState?> FollowSequels(EntryRef start, int hops, CancellationToken cancellationToken) {
        EntryRef current = start;
        for (int hop = 1; hop <= hops; hop++) {
            logger.LogDebug("({ApiName}) Attempting to get season {SeasonNumber}...", tracker.Name, hop + 1);
            var relations = await tracker.GetRelations(current, cancellationToken);
            if (relations == null)
                return null;
            EntryRef? next = SequelIn(relations);
            if (next == null)
                return null;
            current = next;
        }

        // The last hop is read so a sequel that cannot be fetched fails here, not at write time; returning the season
        // we already have would write progress to the wrong entry.
        EntryState? season = await tracker.ReadEntry(current, cancellationToken);
        if (season == null)
            logger.LogInformation("({ApiName}) Could not retrieve season {SeasonNumber}", tracker.Name, hops + 1);
        return season;
    }

    /// <summary>
    /// Walk the sequel chain from the first cour until the cours cover the watched episode. Each cour passed becomes
    /// an earlier part, completed at its length. Moved from UpdateProviderStatus.WalkCours with the same arithmetic.
    /// </summary>
    /// <param name="firstCour">The first cour of the series.</param>
    /// <param name="episodeCount">The episode number as Jellyfin numbers it, counted across all cours.</param>
    public async Task<ChainWalk> Walk(ChainLink firstCour, int episodeCount, CancellationToken cancellationToken) {
        int counter = firstCour.TotalEpisodes;
        int total = 0;
        var parts = new List<ResolvedTarget>();
        ChainLink current = firstCour;
        while (counter < episodeCount) {
            // a cour of unknown length (usually still airing) can't be measured past, so it is the best target
            if (current.TotalEpisodes == 0)
                break;
            // the current cour's sequel is always exactly one hop away
            logger.LogDebug("({ApiName}) Attempting to get season {SeasonNumber}...", tracker.Name, 2);
            var relations = await tracker.GetRelations(current.Entry, cancellationToken);
            EntryRef? sequel = relations == null ? null : SequelIn(relations);
            EntryState? next = sequel == null ? null : await tracker.ReadEntry(sequel, cancellationToken);
            if (next == null) {
                // the episode lies past every cour we could reach; writing it to the last one would exceed its length
                logger.LogWarning("({ApiName}) Could not find a cour covering episode {EpisodeCount}; not updating", tracker.Name, episodeCount);
                return new ChainWalk(null, total, parts, relations == null ? "sequel-fetch-failed" : "no-cour-covers-episode");
            }

            counter += next.TotalEpisodes;
            // complete the current cour; the episode lies past it
            total += current.TotalEpisodes;
            parts.Add(new ResolvedTarget(current.Entry, current.TotalEpisodes, TargetRole.EarlierPart, TitleMatching.Display(current.Titles), current.State));
            current = ChainLink.Of(next);
        }

        return new ChainWalk(current, total, parts, null);
    }

    /// <summary>
    /// Renders what automatic resolution decided as an unsaved match, so saving it keeps today's targets. Where no match
    /// could do that, there is no derived match and the season stays automatic.
    /// </summary>
    /// <param name="source">The source <paramref name="automatic"/> resolved.</param>
    /// <param name="automatic">The automatic answer for <paramref name="source"/>'s episode.</param>
    /// <param name="lastEpisode">The highest source episode the match must cover.</param>
    public async Task<DerivedSegmentation> ToMatch(MatchScope scope, SyncSource source, SourceResolution automatic, int lastEpisode, CancellationToken cancellationToken) {
        if (automatic is not Resolved resolved)
            return new DerivedSegmentation(null, (automatic as Unresolved)?.Reason);
        ResolvedTarget played = resolved.Played;
        var target = new MatchTarget(played.Entry, played.Title);
        // A movie and a special are one episode each; neither route looks past it.
        if (source.Kind == MediaKind.Movie)
            return Derived(scope, [new MatchRange(1, 1, target, 1)]);
        if (source.SeasonNumber == 0)
            return new DerivedSegmentation(new Match(scope, [], [new TargetException(source.EpisodeNumber, target, played.TargetEpisode)]), null);

        // The ID path's offset moves every episode down, so the first entry starts this far into the source.
        int before = source.EpisodeNumber - played.TargetEpisode - resolved.EarlierParts.Sum(part => part.TargetEpisode);
        // The title path applies an offset only to episodes past it, and the ID path can send an episode below 1; ranges can hold neither.
        bool titleOffset = before > 0 && resolved.By is ResolvedBy.TitleSearch or ResolvedBy.SequelChain && resolved.EarlierParts.Count == 0;
        if (played.TargetEpisode < 1 || before < 0 || titleOffset)
            return new DerivedSegmentation(null, "offset-not-representable");
        int last = System.Math.Max(lastEpisode, source.EpisodeNumber);
        return resolved.FollowsChain
            ? await FromChain(scope, resolved, before, last, cancellationToken)
            : await FromOneEntry(scope, played, before, last, cancellationToken);
    }

    /// <summary>This route sends every episode to one entry. A match stops at a known end, which this route does not, so past it there is no match.</summary>
    private async Task<DerivedSegmentation> FromOneEntry(MatchScope scope, ResolvedTarget played, int before, int last, CancellationToken cancellationToken) {
        EntryState? entry = played.State ?? await tracker.ReadEntry(played.Entry, cancellationToken);
        if (entry == null)
            return new DerivedSegmentation(null, "not-found");
        if (entry.TotalEpisodes > 0 && last - before > entry.TotalEpisodes)
            return new DerivedSegmentation(null, "past-entry-end");
        return Derived(scope, [new MatchRange(before + 1, null, new MatchTarget(played.Entry, played.Title), 1)]);
    }

    private async Task<DerivedSegmentation> FromChain(MatchScope scope, Resolved resolved, int before, int last, CancellationToken cancellationToken) {
        ResolvedTarget first = resolved.EarlierParts.Count > 0 ? resolved.EarlierParts[0] : resolved.Played;
        EntryState? state = first.State ?? await tracker.ReadEntry(first.Entry, cancellationToken);
        if (state == null)
            return new DerivedSegmentation(null, "not-found");
        var walk = await Walk(new ChainLink(first.Entry, state.TotalEpisodes, state.Titles, state), last - before, cancellationToken);
        if (walk.Target == null)
            return new DerivedSegmentation(null, walk.FailureReason);

        var ranges = new List<MatchRange>();
        int start = before + 1;
        foreach (ResolvedTarget part in walk.EarlierParts) {
            ranges.Add(new MatchRange(start, start + part.TargetEpisode - 1, new MatchTarget(part.Entry, part.Title), 1));
            start += part.TargetEpisode;
        }

        int total = walk.Target.TotalEpisodes;
        // A cour of unknown length is usually still airing, so its range stays open.
        ranges.Add(new MatchRange(start, total > 0 ? start + total - 1 : null,
            new MatchTarget(walk.Target.Entry, TitleMatching.Display(walk.Target.Titles)), 1));
        return Derived(scope, ranges);
    }

    private static DerivedSegmentation Derived(MatchScope scope, IReadOnlyList<MatchRange> ranges) =>
        new(new Match(scope, ranges, []), null);

    private EntryRef? SequelIn(IReadOnlyList<EntryRelation> relations) {
        EntryRef? sequel = relations.FirstOrDefault(relation => relation.Kind == RelationKind.Sequel)?.Entry;
        if (sequel == null)
            logger.LogInformation("({ApiName}) Could not find any related anime sequel", tracker.Name);
        return sequel;
    }
}
