#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// An in-memory tracker standing in for one provider's <see cref="IApiCallHelpers"/>. It follows the limits of that
/// provider's real client: Simkl cannot search or read by numeric ID, Annict and Shikimori read and write only with an
/// alternative ID, Simkl writes need cross IDs and the media kind, and MAL reads never carry a rewatch count. Reads
/// return copies; writes update the stored entry and are recorded in order.
/// </summary>
internal sealed class FakeTracker(ApiName provider) : IApiCallHelpers {
    private readonly Dictionary<int, Anime> _entries = new();

    public ApiName Provider => provider;

    public List<RecordedWrite> Writes { get; } = new();

    /// <summary>The identifiers each write was sent with, in the same order as <see cref="Writes"/>.</summary>
    public List<WriteContext> WriteContexts { get; } = new();

    /// <summary>What every search returns, regardless of the query, in order.</summary>
    public List<Anime> SearchResults { get; } = new();

    /// <summary>Entry IDs whose lookup returns null, as a transient provider error does.</summary>
    public HashSet<int> FailingIds { get; } = new();

    /// <summary>1-based numbers of write calls that return no receipt, as an unconfirmed request does.</summary>
    public HashSet<int> FailingWrites { get; } = new();

    /// <summary>Every numeric-ID read, in order, including reads that return null.</summary>
    public List<(int Id, bool Related)> Reads { get; } = new();

    private bool UsesAlternativeIds => provider is ApiName.Annict or ApiName.Shikimori;

    public Anime Add(Anime anime) {
        _entries[anime.Id] = Copy(anime, forRead: false);
        return anime;
    }

    public Task<List<Anime>> SearchAnime(string query) {
        if (provider == ApiName.Simkl)
            return Task.FromResult<List<Anime>>(null);
        return Task.FromResult(SearchResults
            .Select(result => Copy(_entries.TryGetValue(result.Id, out var stored) ? stored : result, forRead: true))
            .ToList());
    }

    public Task<Anime> GetAnime(int id, string alternativeId = null, bool getRelated = false) {
        Reads.Add((id, getRelated));
        if (provider == ApiName.Simkl || (UsesAlternativeIds && alternativeId == null) || FailingIds.Contains(id) || !_entries.TryGetValue(id, out var anime))
            return Task.FromResult<Anime>(null);
        return Task.FromResult(Copy(anime, forRead: true));
    }

    // Only Simkl resolves through cross IDs; every fixture keys its entry by the MAL ID.
    public Task<Anime> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false) {
        if (provider != ApiName.Simkl || ids?.MyAnimeList is not int id || FailingIds.Contains(id) || !_entries.TryGetValue(id, out var anime))
            return Task.FromResult<Anime>(null);
        return Task.FromResult(Copy(anime, forRead: true));
    }

    public Task<UpdateAnimeStatusResponse> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
        bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null,
        string alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids = null, bool? isShow = null) {
        // The real client sends nothing without the identifiers this provider writes by.
        if ((UsesAlternativeIds && alternativeId == null) || (provider == ApiName.Simkl && (ids == null || isShow == null)))
            return Task.FromResult<UpdateAnimeStatusResponse>(null);
        Writes.Add(new RecordedWrite(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched,
            startDate != null, endDate != null));
        WriteContexts.Add(new WriteContext(alternativeId, ids?.MyAnimeList, isShow));
        if (FailingWrites.Contains(Writes.Count))
            return Task.FromResult<UpdateAnimeStatusResponse>(null);
        int? previousCount = null;
        if (_entries.TryGetValue(animeId, out var stored)) {
            previousCount = stored.MyListStatus?.RewatchCount;
            stored.MyListStatus = new MyListStatus {
                Status = status,
                NumEpisodesWatched = numberOfWatchedEpisodes,
                IsRewatching = isRewatching ?? stored.MyListStatus?.IsRewatching ?? false,
                RewatchCount = numberOfTimesRewatched ?? previousCount
            };
        }
        // Echo what was written, as a tracker that stores the values does. The count comes from the server's record,
        // which MAL reports in its write response even though its reads do not.
        return Task.FromResult(new UpdateAnimeStatusResponse {
            UsesAcknowledgementFields = true,
            AcknowledgedProgress = numberOfWatchedEpisodes,
            AcknowledgedStatus = status,
            AcknowledgedRewatching = isRewatching,
            AcknowledgedRewatchCount = numberOfTimesRewatched ?? previousCount,
            NumTimesRewatched = numberOfTimesRewatched ?? previousCount ?? 0
        });
    }

    public Task<MalApiCalls.User> GetUser() => throw new NotSupportedException("Sync to trackers never reads the user.");

    public Task<List<Anime>> GetAnimeList(Status status, int? userId = null) =>
        throw new NotSupportedException("Sync to trackers never lists entries.");

    private Anime Copy(Anime source, bool forRead) => new() {
        Id = source.Id,
        AlternativeId = AlternativeIdFor(source),
        Title = source.Title,
        AlternativeTitles = source.AlternativeTitles,
        NumEpisodes = source.NumEpisodes,
        RelatedAnime = source.RelatedAnime?.Select(related => new RelatedAnime {
            RelationType = related.RelationType,
            Anime = new Anime { Id = related.Anime.Id, AlternativeId = AlternativeIdFor(related.Anime) }
        }).ToList(),
        MyListStatus = source.MyListStatus == null ? null : new MyListStatus {
            Status = source.MyListStatus.Status,
            NumEpisodesWatched = source.MyListStatus.NumEpisodesWatched,
            IsRewatching = source.MyListStatus.IsRewatching,
            // MAL's my_list_status has no field the client maps to RewatchCount, so MAL reads never carry one.
            RewatchCount = forRead && provider == ApiName.Mal ? null : source.MyListStatus.RewatchCount
        }
    };

    private string AlternativeIdFor(Anime anime) => anime.AlternativeId ?? (UsesAlternativeIds ? $"alt-{anime.Id}" : null);
}

internal sealed record RecordedWrite(int EntryId, int Progress, Status Status, bool? Rewatching, int? RewatchCount,
    bool StartDate, bool EndDate) {
    public override string ToString() =>
        $"write(id={EntryId}, progress={Progress}, status={Status}, rewatching={Format(Rewatching)}, count={Format(RewatchCount)}, start={StartDate}, end={EndDate})";

    private static string Format<T>(T? value) where T : struct => value?.ToString() ?? "-";
}

internal sealed record WriteContext(string AlternativeId, int? MalId, bool? IsShow);
