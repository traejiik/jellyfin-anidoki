using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki.Api.Annict;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Annict;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

public sealed class AnnictTrackerClient(AnnictApiCalls api) : ITrackerClient {
    public async Task<List<Anime>?> SearchAnime(string query) => ConvertSearchResults(await api.SearchAnime(query));

    internal static List<Anime> ConvertSearchResults(List<AnnictSearch.AnnictAnime>? animeList) {
        var converted = new List<Anime>();
        if (animeList != null) {
            foreach (AnnictSearch.AnnictAnime anime in animeList)
                converted.Add(ClassConversions.ConvertAnnictAnime(anime));
        }

        return converted;
    }

    public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) {
        if (alternativeId == null)
            return null;
        var anime = await api.GetAnime(alternativeId);
        return anime == null ? null : ClassConversions.ConvertAnnictAnime(anime);
    }

    public Task<Anime?> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        Task.FromResult<Anime?>(null);

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null) {
        if (alternativeId == null)
            return null;
        AnnictSearch.AnnictMediaStatus annictStatus = status switch {
            Status.Watching => AnnictSearch.AnnictMediaStatus.Watching,
            Status.Completed or Status.Rewatching => AnnictSearch.AnnictMediaStatus.Watched,
            Status.On_hold => AnnictSearch.AnnictMediaStatus.On_hold,
            Status.Dropped => AnnictSearch.AnnictMediaStatus.Stop_watching,
            Status.Plan_to_watch => AnnictSearch.AnnictMediaStatus.Wanna_watch,
            _ => AnnictSearch.AnnictMediaStatus.No_state
        };
        if (!await api.UpdateAnime(alternativeId, annictStatus))
            return null;
        return new UpdateAnimeStatusResponse { UsesAcknowledgementFields = true, AcknowledgedStatus = status == Status.Rewatching ? Status.Completed : status };
    }
}
