using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

/// <summary>
/// One provider's API in the shape <see cref="TrackerProvider"/> works with. Every method returns null when the request
/// failed or the provider has no such call; failure kinds reach the adapter through <see cref="TrackerCallScope"/>.
/// </summary>
public interface ITrackerClient {
    Task<List<Anime>?> SearchAnime(string query);

    Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false);

    Task<Anime?> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false);

    Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
        bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null,
        string? alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null);
}
