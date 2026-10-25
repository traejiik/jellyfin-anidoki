using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// Follows a tracker's sequel relations, where Jellyfin numbers one season and the tracker splits it into entries.
/// Reads only. Phase 3 PR 13 adds mapping-driven segments here.
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

    private EntryRef? SequelIn(IReadOnlyList<EntryRelation> relations) {
        EntryRef? sequel = relations.FirstOrDefault(relation => relation.Kind == RelationKind.Sequel)?.Entry;
        if (sequel == null)
            logger.LogInformation("({ApiName}) Could not find any related anime sequel", tracker.Name);
        return sequel;
    }
}
