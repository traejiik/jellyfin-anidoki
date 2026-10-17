#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// An in-memory tracker standing in for one provider's <see cref="IApiCallHelpers"/>. Reads return copies, as a
/// real provider returns fresh objects; writes update the stored entry and are recorded in order.
/// </summary>
internal sealed class FakeTracker : IApiCallHelpers {
    private readonly Dictionary<int, Anime> _entries = new();

    public List<RecordedWrite> Writes { get; } = new();

    /// <summary>What every search returns, regardless of the query, in order.</summary>
    public List<Anime> SearchResults { get; } = new();

    /// <summary>Entry IDs whose lookup returns null, as a transient provider error does.</summary>
    public HashSet<int> FailingIds { get; } = new();

    /// <summary>Stores a copy, so trackers seeded from the same fixture never share state.</summary>
    public Anime Add(Anime anime) {
        _entries[anime.Id] = Copy(anime);
        return anime;
    }

    public Task<List<Anime>> SearchAnime(string query) => Task.FromResult(SearchResults.Select(Copy).ToList());

    public Task<Anime> GetAnime(int id, string alternativeId = null, bool getRelated = false) =>
        Task.FromResult(FailingIds.Contains(id) || !_entries.TryGetValue(id, out var anime) ? null : Copy(anime));

    // Simkl resolves through cross IDs; every fixture keys its entry by the MAL ID.
    public Task<Anime> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        GetAnime(ids?.MyAnimeList ?? 0);

    public Task<UpdateAnimeStatusResponse> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
        bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null,
        string alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids = null, bool? isShow = null) {
        Writes.Add(new RecordedWrite(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched,
            startDate != null, endDate != null));
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
        // Echo what was written, as a tracker that stores the values does.
        return Task.FromResult(new UpdateAnimeStatusResponse {
            UsesAcknowledgementFields = true,
            AcknowledgedProgress = numberOfWatchedEpisodes,
            AcknowledgedStatus = status,
            AcknowledgedRewatching = isRewatching,
            AcknowledgedRewatchCount = numberOfTimesRewatched,
            NumTimesRewatched = numberOfTimesRewatched ?? previousCount ?? 0
        });
    }

    public Task<MalApiCalls.User> GetUser() => throw new NotSupportedException("Sync to trackers never reads the user.");

    public Task<List<Anime>> GetAnimeList(Status status, int? userId = null) =>
        throw new NotSupportedException("Sync to trackers never lists entries.");

    private static Anime Copy(Anime source) => new() {
        Id = source.Id,
        AlternativeId = source.AlternativeId,
        Title = source.Title,
        AlternativeTitles = source.AlternativeTitles,
        NumEpisodes = source.NumEpisodes,
        RelatedAnime = source.RelatedAnime,
        MyListStatus = source.MyListStatus == null ? null : new MyListStatus {
            Status = source.MyListStatus.Status,
            NumEpisodesWatched = source.MyListStatus.NumEpisodesWatched,
            IsRewatching = source.MyListStatus.IsRewatching,
            RewatchCount = source.MyListStatus.RewatchCount
        }
    };
}

internal sealed record RecordedWrite(int EntryId, int Progress, Status Status, bool? Rewatching, int? RewatchCount,
    bool StartDate, bool EndDate) {
    public override string ToString() =>
        $"write(id={EntryId}, progress={Progress}, status={Status}, rewatching={Format(Rewatching)}, count={Format(RewatchCount)}, start={StartDate}, end={EndDate})";

    private static string Format<T>(T? value) where T : struct => value?.ToString() ?? "-";
}
