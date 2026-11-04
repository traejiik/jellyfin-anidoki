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

    public async Task<Anime?> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false) {
        SimklIdLookupMedia? lookup = await api.GetAnimeByIdLookup(ids, title);
        if (lookup?.Ids?.Simkl is null or 0)
            return null;
        var detailed = await api.GetAnime(lookup.Ids.Simkl);
        var userList = await api.GetUserAnimeList();
        return detailed == null
            ? null
            : ClassConversions.ConvertSimklAnime(detailed, userList?.FirstOrDefault(entry => entry.Show.Ids.Simkl == detailed.Ids.Simkl));
    }

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null) {
        if (isShow == null || ids == null)
            return null;
        SimklStatus simklStatus = status switch {
            Status.Completed => SimklStatus.completed,
            Status.Dropped => SimklStatus.dropped,
            Status.On_hold => SimklStatus.hold,
            Status.Plan_to_watch => SimklStatus.plantowatch,
            _ => SimklStatus.watching
        };
        return await api.UpdateAnimeReceipt(animeId, simklStatus, isShow.Value, ids, numberOfWatchedEpisodes);
    }
}
