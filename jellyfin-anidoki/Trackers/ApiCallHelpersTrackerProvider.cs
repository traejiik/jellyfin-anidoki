using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

/// <summary>
/// Adapts one provider's <see cref="IApiCallHelpers"/> client to <see cref="ITrackerProvider"/>. Provider differences in
/// list state and write sequences live here. Phase 3 replaces the wrapped client with direct provider calls.
/// </summary>
public sealed class ApiCallHelpersTrackerProvider(ApiName name, IApiCallHelpers client, TimeProvider clock) : ITrackerProvider {
    public ApiName Name => name;

    public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.For(name);

    public async Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        Anime? anime = entry.Lookup != null
            ? await client.GetAnime(ToClientIds(entry.Lookup.Ids), entry.Lookup.Title)
            : await client.GetAnime(entry.Id, entry.AlternativeId);
        if (anime == null)
            return null;
        return new EntryState(entry, anime.NumEpisodes, ToListState(anime.MyListStatus), clock.GetUtcNow(), Titles(anime));
    }

    public async Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        List<Anime>? results = await client.SearchAnime(title);
        return (results ?? [])
            .Select(anime => new EntryCandidate(new EntryRef(name, anime.Id, anime.AlternativeId), Titles(anime), anime.NumEpisodes))
            .ToArray();
    }

    public async Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Lookup != null)
            return null;
        Anime? anime = await client.GetAnime(entry.Id, entry.AlternativeId, getRelated: true);
        if (anime == null)
            return null;
        return (anime.RelatedAnime ?? [])
            .Where(related => related.Anime != null)
            .Select(related => new EntryRelation(ToRelationKind(related.RelationType), new EntryRef(name, related.Anime.Id, related.Anime.AlternativeId)))
            .ToArray();
    }

    public async Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        switch (name) {
            case ApiName.Mal:
                return Direct(ids.Mal);
            case ApiName.AniList:
                return Direct(ids.AniList);
            case ApiName.Kitsu:
                return Direct(ids.Kitsu);
            case ApiName.Simkl:
                if (ids.AniList is not > 0 && ids.Mal is not > 0 && ids.Kitsu is not > 0 && ids.AniDb is not > 0)
                    return null;
                Anime? anime = await client.GetAnime(ToClientIds(ids), title);
                return anime == null ? null : new EntryRef(name, anime.Id, anime.AlternativeId, new CrossReference(ids, title, kind));
            default:
                // Annict and Shikimori are matched on the MAL ID among their search results.
                if (ids.Mal is not > 0)
                    return null;
                var candidates = await Search(title, cancellationToken);
                return candidates.FirstOrDefault(candidate => candidate.Entry.Id == ids.Mal)?.Entry;
        }
    }

    public async Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) {
        if (change.Rewatch != RewatchTransition.None) {
            if (!Capabilities.SupportsRewatch)
                throw new ArgumentException($"{name} does not support rewatching", nameof(change));
            if (change.Status != TrackerStatus.Completed)
                throw new ArgumentException("A rewatch change must target Completed", nameof(change));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Status-only providers ignore progress; their status write has always sent 1.
        int progress = Capabilities.TracksProgress ? change.Progress : 1;
        switch (change.Rewatch) {
            case RewatchTransition.Start:
                switch (name) {
                    case ApiName.AniList:
                        // AniList does not accept rewatching and progress in one call.
                        var first = await Write(current, progress, Status.Completed, true, null, change, cancellationToken);
                        if (first == null)
                            return null;
                        var second = await Write(current, progress, Status.Completed, true, null, change, cancellationToken);
                        return second ?? first with { Complete = false };
                    case ApiName.Kitsu:
                        return await Write(current, progress, Status.Rewatching, true, null, change, cancellationToken);
                    case ApiName.Shikimori:
                        return await Write(current, progress, Status.Rewatching, null, null, change, cancellationToken);
                    default:
                        return await Write(current, progress, Status.Completed, true, null, change, cancellationToken);
                }
            case RewatchTransition.Continue:
                return name switch {
                    ApiName.Mal => await Write(current, progress, Status.Completed, true, null, change, cancellationToken),
                    // Kitsu keeps reconsuming set on the entry; progress alone continues the rewatch.
                    ApiName.Kitsu => await Write(current, progress, Status.Watching, null, null, change, cancellationToken),
                    _ => await Write(current, progress, Status.Rewatching, null, null, change, cancellationToken)
                };
            case RewatchTransition.Finish:
                int? count = current.List?.RewatchCount;
                if (count != null)
                    return await Write(current, progress, Status.Completed, false, count + 1, change, cancellationToken);
                // MAL's reads carry no count, so finish first and count from the receipt.
                var finished = await Write(current, progress, Status.Completed, false, null, change, cancellationToken);
                if (finished?.RewatchCount == null)
                    return finished == null ? null : finished with { Complete = false };
                var counted = await Write(current, progress, Status.Completed, false, finished.RewatchCount + 1, change, cancellationToken);
                return counted ?? finished with { Complete = false };
            default:
                // Completing an entry already on the list clears any rewatch flag the provider kept, as the sync always has.
                bool? cleared = change.Status == TrackerStatus.Completed && current.List != null && Capabilities.TracksProgress ? false : null;
                return await Write(current, progress, ToClientStatus(change.Status), cleared, null, change, cancellationToken);
        }
    }

    private async Task<WriteReceipt?> Write(EntryState current, int progress, Status status, bool? rewatching, int? rewatchCount,
        EntryChange change, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        EntryRef entry = current.Entry;
        bool? isShow = entry.Lookup == null ? null : entry.Lookup.Kind == MediaKind.Series;
        UpdateAnimeStatusResponse? response = await client.UpdateAnime(entry.Id, progress, status, rewatching, rewatchCount,
            change.StartDate, change.EndDate, entry.AlternativeId, entry.Lookup == null ? null : ToClientIds(entry.Lookup.Ids), isShow);
        return response == null ? null : ToReceipt(response);
    }

    private WriteReceipt ToReceipt(UpdateAnimeStatusResponse response) {
        int? progress = response.UsesAcknowledgementFields ? response.AcknowledgedProgress : response.NumEpisodesWatched;
        Status? status = response.UsesAcknowledgementFields ? response.AcknowledgedStatus : response.Status;
        bool? rewatching = response.UsesAcknowledgementFields ? response.AcknowledgedRewatching : response.IsRewatching;
        int? count = response.UsesAcknowledgementFields ? response.AcknowledgedRewatchCount : response.NumTimesRewatched;
        if (status == Status.Rewatching)
            rewatching = true;
        return new WriteReceipt(Capabilities.TracksProgress ? progress : null, status == null ? null : ToTrackerStatus(status.Value),
            rewatching, count, true);
    }

    private EntryRef? Direct(int? id) => id is > 0 ? new EntryRef(name, id.Value, null) : null;

    private ListState? ToListState(MyListStatus? status) {
        if (status == null)
            return null;
        // Kitsu's client reports a missing library entry as an empty status with no rewatch count.
        if (name == ApiName.Kitsu && status.RewatchCount == null)
            return null;
        bool rewatching = status.IsRewatching || status.Status == Status.Rewatching;
        return new ListState(rewatching ? TrackerStatus.Completed : ToTrackerStatus(status.Status), status.NumEpisodesWatched,
            rewatching, Capabilities.ReportsRewatchCount ? status.RewatchCount : null);
    }

    private static EntryTitles Titles(Anime anime) => new(anime.Title, anime.AlternativeTitles?.En, anime.AlternativeTitles?.Ja,
        anime.AlternativeTitles?.Synonyms?.ToArray() ?? []);

    internal static TrackerStatus ToTrackerStatus(Status status) => status switch {
        Status.Watching => TrackerStatus.Watching,
        Status.Completed or Status.Rewatching => TrackerStatus.Completed,
        Status.On_hold => TrackerStatus.OnHold,
        Status.Dropped => TrackerStatus.Dropped,
        Status.Plan_to_watch => TrackerStatus.PlanToWatch,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    internal static Status ToClientStatus(TrackerStatus status) => status switch {
        TrackerStatus.Watching => Status.Watching,
        TrackerStatus.Completed => Status.Completed,
        TrackerStatus.OnHold => Status.On_hold,
        TrackerStatus.Dropped => Status.Dropped,
        TrackerStatus.PlanToWatch => Status.Plan_to_watch,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static RelationKind ToRelationKind(RelationType type) => type switch {
        RelationType.Sequel => RelationKind.Sequel,
        RelationType.Prequel => RelationKind.Prequel,
        RelationType.Side_Story => RelationKind.SideStory,
        RelationType.Alternative_Version => RelationKind.AlternativeVersion,
        RelationType.Alternative_Setting => RelationKind.AlternativeSetting,
        _ => RelationKind.Other
    };

    private static AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ToClientIds(ResolvedIds ids) => new() {
        Anilist = ids.AniList,
        MyAnimeList = ids.Mal,
        Kitsu = ids.Kitsu,
        AniDb = ids.AniDb
    };
}
