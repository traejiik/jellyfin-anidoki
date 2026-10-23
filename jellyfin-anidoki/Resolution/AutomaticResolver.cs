using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Resolution;

/// <summary>Finds the tracker entry and episode for a source on one provider. Reads only.</summary>
public interface ISourceResolver {
    Task<SourceResolution> Resolve(SyncSource source, SourceIds ids, ITrackerProvider tracker, CancellationToken cancellationToken);
}

/// <summary>
/// Resolution with no saved matches: resolved IDs where the provider takes them, then title search with the sequel
/// chain and specials. Moved from UpdateProviderStatus.UpdateCore; each branch keeps today's order and conditions.
/// </summary>
public sealed class AutomaticResolver(ILogger logger) : ISourceResolver {
    private static readonly IReadOnlyList<ResolvedTarget> None = [];

    public async Task<SourceResolution> Resolve(SyncSource source, SourceIds ids, ITrackerProvider tracker, CancellationToken cancellationToken) {
        var chain = new RelationChainSegmenter(tracker, logger);
        bool useIds = ids.UseForSource(source);
        int? direct = tracker.Name switch {
            ApiName.Mal => Set(ids.Ids.MyAnimeList),
            ApiName.AniList => Set(ids.Ids.Anilist),
            ApiName.Kitsu => Set(ids.Ids.Kitsu),
            _ => null
        };
        if (direct is int id && useIds)
            return await ById(id, source, ids, tracker, chain, cancellationToken);
        // The series or movie's own AniList ID, when the ID databases gave none. Season 1 only (SyncSource checks).
        if (tracker.Name == ApiName.AniList && source.FallbackAniListId is int fallback)
            return await ById(fallback, source, ids, tracker, chain, cancellationToken);
        // Movies stay off Simkl's ID path: its movie write has not been checked against a real account.
        if (tracker.Name == ApiName.Simkl && source.Kind == MediaKind.Series && useIds && HasAny(ids.Ids))
            return await ByCrossIds(source, ids, tracker, cancellationToken);
        // Annict works differently to the other providers, so it always goes the traditional route.
        return await ByTitle(source, ids, tracker, chain, cancellationToken);
    }

    /// <summary>
    /// An entry resolved by ID. A tracker can split a season the ID databases keep whole (AniList splits Steel Ball Run
    /// into "STAGE" entries AniDB lists as one), so an episode past the entry's known length follows the sequel chain,
    /// as the title-search path does.
    /// </summary>
    private async Task<SourceResolution> ById(int id, SyncSource source, SourceIds ids, ITrackerProvider tracker,
        RelationChainSegmenter chain, CancellationToken cancellationToken) {
        int progress = ids.IdProgress(source);
        EntryState? entry = await tracker.ReadEntry(new EntryRef(tracker.Name, id, null), cancellationToken);
        if (entry == null) {
            logger.LogInformation("({ApiName}) Could not retrieve the matched anime from the provider; not updating", tracker.Name);
            return new Unresolved("not-found", new UnresolvedRecord(id.ToString(), ""), None);
        }

        if (source.Kind == MediaKind.Series && entry.TotalEpisodes > 0 && entry.TotalEpisodes < progress) {
            logger.LogDebug("({ApiName}) Watched episode passes total episodes in the resolved entry! Checking for additional seasons/cours...", tracker.Name);
            var walk = await chain.Walk(ChainLink.Of(entry), progress, cancellationToken);
            if (walk.Target == null)
                return new Unresolved(walk.FailureReason!, Record(ChainLink.Of(entry)), walk.EarlierParts);
            return new Resolved(Played(walk.Target, progress - walk.EpisodesBefore), ResolvedBy.SequelChain, walk.EarlierParts);
        }

        return new Resolved(Played(ChainLink.Of(entry), progress), ResolvedBy.Ids, None);
    }

    /// <summary>Simkl reads and writes by cross IDs; it takes the ID path's episode with no length check or walk.</summary>
    private async Task<SourceResolution> ByCrossIds(SyncSource source, SourceIds ids, ITrackerProvider tracker, CancellationToken cancellationToken) {
        EntryRef? entry = await tracker.FindByCrossIds(ids.ToResolvedIds(), source.SearchTitle, MediaKind.Series, cancellationToken);
        if (entry == null) {
            logger.LogInformation("({ApiName}) Could not retrieve the matched anime from the provider; not updating", tracker.Name);
            return new Unresolved("not-found", new UnresolvedRecord("", source.SearchTitle ?? ""), None);
        }

        return new Resolved(new ResolvedTarget(entry, ids.IdProgress(source), TargetRole.Played, source.SearchTitle ?? ""), ResolvedBy.CrossIds, None);
    }

    private async Task<SourceResolution> ByTitle(SyncSource source, SourceIds ids, ITrackerProvider tracker,
        RelationChainSegmenter chain, CancellationToken cancellationToken) {
        string kind = source.Kind == MediaKind.Series ? "series" : "movie";
        logger.LogDebug("({ApiName}) Searching for {AnimeType}: {SearchTitle}", tracker.Name, kind, source.SearchTitle);
        int? mal = ids.Ids.MyAnimeList;
        foreach (var candidate in await tracker.Search(source.SearchTitle, cancellationToken)) {
            // Annict, and Shikimori when the IDs give a MAL ID, match on it instead of titles and skip season handling.
            bool byMalId = tracker.Name == ApiName.Annict || (tracker.Name == ApiName.Shikimori && mal != null && candidate.Entry.Id > 0);
            if (byMalId ? mal == null || candidate.Entry.Id != mal : !TitleMatching.Matches(candidate.Titles, source.CandidateTitles))
                continue;
            logger.LogDebug("({ApiName}) Found matching {AnimeType}: {Title}", tracker.Name, kind, TitleMatching.Display(candidate.Titles));
            var match = ChainLink.Of(candidate);
            int episode = ids.TitleProgress(source);
            if (source.Kind == MediaKind.Movie || byMalId)
                return new Resolved(Played(match, episode), ResolvedBy.TitleSearch, None);
            if (source.SeasonNumber > 1) {
                // if this is not the first season, then we need to look up the related season.
                var season = await chain.FollowSequels(candidate.Entry, source.SeasonNumber - 1, cancellationToken);
                if (season == null) {
                    logger.LogWarning("({ApiName}) Could not find next season", tracker.Name);
                    return new Unresolved("sequel-fetch-failed", null, None);
                }

                logger.LogDebug("({ApiName}) Season being watched is {Title}", tracker.Name, TitleMatching.Display(season.Titles));
                return new Resolved(Played(ChainLink.Of(season), episode), ResolvedBy.SequelChain, None);
            }

            if (source.SeasonNumber == 0)
                return await Special(candidate.Entry, source, episode, tracker, cancellationToken);

            if (match.TotalEpisodes > 0 && match.TotalEpisodes < source.EpisodeNumber) {
                // TotalEpisodes is a non-nullable int, so an unknown
                // episode count arrives as 0 - which is the norm for a
                // currently-airing show. Without the > 0 guard this branch
                // fires on every episode of every airing series that reaches
                // the title-search fallback.
                //
                // Walking cannot produce a correct answer in that case
                // regardless: the walk computes the next cour's episode
                // number as episodeCount - totalEpisodesWatched, and
                // totalEpisodesWatched accumulates each cour's length. With
                // 0 the offset is always 0, so the raw episode number gets
                // sent to the sequel. The arithmetic requires a known
                // length, so an entry of unknown length is written as the
                // first cour without walking.
                //
                // The comparison and the walk use Jellyfin's raw episode number, not the offset one.
                logger.LogDebug("({ApiName}) Watched episode passes total episodes in season! Checking for additional seasons/cours...", tracker.Name);
                var walk = await chain.Walk(match, source.EpisodeNumber, cancellationToken);
                if (walk.Target == null)
                    return new Unresolved(walk.FailureReason!, Record(match), walk.EarlierParts);
                return new Resolved(Played(walk.Target, source.EpisodeNumber - walk.EpisodesBefore), ResolvedBy.SequelChain, walk.EarlierParts);
            }

            return new Resolved(Played(match, episode), ResolvedBy.TitleSearch, None);
        }

        logger.LogWarning("({ApiName}) Series not found", tracker.Name);
        return new Unresolved("no-title-match", new UnresolvedRecord("", source.SearchTitle ?? ""), None);
    }

    /// <summary>
    /// The episode is an OVA or special: a side story or alternative version of the match whose title roughly contains
    /// the episode's name.
    /// </summary>
    private async Task<SourceResolution> Special(EntryRef match, SyncSource source, int episode, ITrackerProvider tracker,
        CancellationToken cancellationToken) {
        // An episode without a name can match nothing (today this threw); an empty name still matches as before.
        string? name = source.EpisodeName;
        var relations = name == null ? null : await tracker.GetRelations(match, cancellationToken);
        foreach (var relation in relations ?? []) {
            if (relation.Kind is not (RelationKind.SideStory or RelationKind.AlternativeVersion or RelationKind.AlternativeSetting))
                continue;
            EntryState? related = await tracker.ReadEntry(relation.Entry, cancellationToken);
            if (related?.Titles is { Main: { } main, English: { } english } titles &&
                (TitleMatching.ContainsExtended(main, name!) ||
                 TitleMatching.ContainsExtended(english, name!) ||
                 (titles.Japanese != null && TitleMatching.ContainsExtended(titles.Japanese, name!)))) {
                // rough match
                return new Resolved(Played(ChainLink.Of(related), episode), ResolvedBy.TitleSearch, None);
            }
        }

        logger.LogWarning("({ApiName}) Could not find OVA", tracker.Name);
        return new Unresolved("no-special-match", null, None);
    }

    private static ResolvedTarget Played(ChainLink link, int episode) =>
        new(link.Entry, episode, TargetRole.Played, TitleMatching.Display(link.Titles));

    private static UnresolvedRecord Record(ChainLink link) =>
        new(link.Entry.AlternativeId ?? link.Entry.Id.ToString(), TitleMatching.Display(link.Titles));

    // A resolved ID counts when it is set and not 0, as the checks this replaced read.
    private static int? Set(int? id) => id is null or 0 ? null : id;

    private static bool HasAny(Helpers.AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids) =>
        Set(ids.MyAnimeList) != null || Set(ids.Anilist) != null || Set(ids.Kitsu) != null || Set(ids.AniDb) != null;
}
