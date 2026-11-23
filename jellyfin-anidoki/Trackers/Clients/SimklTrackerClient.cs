using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Api.Simkl;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Models.Simkl;

namespace jellyfin_anidoki.Trackers;

/// <summary>Simkl reads and writes by cross IDs; it has no numeric-ID read, and sync never searches it.</summary>
public sealed class SimklTrackerClient(SimklApiCalls api) : ITrackerClient {
    public Task<List<Anime>?> SearchAnime(string query) => Task.FromResult<List<Anime>?>(null);

    public Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) => Task.FromResult<Anime?>(null);

    public async Task<Anime?> GetAnime(ArmClient.OfflineDatabaseResponse ids, string title, bool getRelated = false) {
        SimklIdLookupMedia? lookup = await api.GetAnimeByIdLookup(ids, title);
        if (lookup?.Ids?.Simkl is null or 0)
            return null;
        var detailed = await api.GetAnime(lookup.Ids.Simkl);
        var userList = await api.GetUserAnimeList();
        // A list that could not be read must not look like a title that is not on it (D8): the adapter fails the read.
        if (userList == null)
            TrackerCallScope.Report(TrackerFailureKind.Transient);
        return detailed == null
            ? null
            : ClassConversions.ConvertSimklAnime(detailed, userList?.FirstOrDefault(entry => entry.Show?.Ids?.Simkl == detailed.Ids?.Simkl));
    }

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        ArmClient.OfflineDatabaseResponse? ids = null, bool? isShow = null, int? scoreRaw = null, bool clearScore = false) {
        if (isShow == null || ids == null)
            return null;
        SimklStatus simklStatus = status switch {
            Status.Completed => SimklStatus.completed,
            Status.Dropped => SimklStatus.dropped,
            Status.On_hold => SimklStatus.hold,
            Status.Plan_to_watch => SimklStatus.plantowatch,
            _ => SimklStatus.watching
        };
        UpdateAnimeStatusResponse? response = await api.UpdateAnimeReceipt(animeId, simklStatus, isShow.Value, ids, numberOfWatchedEpisodes);
        if (response == null || (scoreRaw == null && !clearScore))
            return response;
        // Simkl keeps ratings apart from the list, so the rating is a second call after a confirmed list write. If it fails,
        // the list write stands and the missing acknowledgement marks the change incomplete.
        int? rating = clearScore ? null : (int?)ScoreScale.ToDisplay(scoreRaw, ScoreFormat.Point10);
        if (await api.SetRating(animeId, ids, isShow.Value, rating))
            response.AcknowledgedScoreRaw = rating * 10 ?? 0;
        return response;
    }
}
