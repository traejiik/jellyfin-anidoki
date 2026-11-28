using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers.Mal;

namespace jellyfin_anidoki.Trackers.Shikimori;

public sealed class ShikimoriTrackerClient(ShikimoriApiCalls api) : ITrackerClient {
    public async Task<List<Anime>?> SearchAnime(string query) {
        bool updateNsfw = Plugin.Instance?.PluginConfiguration?.updateNsfw == true;
        return ConvertSearchResults(await api.SearchAnime(query), updateNsfw);
    }

    internal static List<Anime> ConvertSearchResults(List<ShikimoriAnime>? animeList, bool updateNsfw) {
        var converted = new List<Anime>();
        if (animeList == null)
            return converted;
        foreach (ShikimoriAnime anime in animeList) {
            if (!updateNsfw && anime.IsCensored == true)
                continue;
            converted.Add(ClassConversions.ConvertShikimoriAnime(anime));
        }

        return converted;
    }

    public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) {
        if (alternativeId == null)
            return null;
        var anime = await api.GetAnime(alternativeId, getRelated);
        return anime == null ? null : ClassConversions.ConvertShikimoriAnime(anime);
    }

    public Task<Anime?> GetAnime(ArmClient.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        Task.FromResult<Anime?>(null);

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        ArmClient.OfflineDatabaseResponse? ids = null, bool? isShow = null, int? scoreRaw = null, bool clearScore = false) {
        if (alternativeId == null)
            return null;
        ShikimoriUserRate.StatusEnum shikimoriStatus = status switch {
            Status.Watching => ShikimoriUserRate.StatusEnum.watching,
            Status.Completed => ShikimoriUserRate.StatusEnum.completed,
            Status.Rewatching => ShikimoriUserRate.StatusEnum.rewatching,
            Status.On_hold => ShikimoriUserRate.StatusEnum.on_hold,
            Status.Dropped => ShikimoriUserRate.StatusEnum.dropped,
            Status.Plan_to_watch => ShikimoriUserRate.StatusEnum.planned,
            _ => ShikimoriUserRate.StatusEnum.watching
        };
        int? score = clearScore ? 0 : (int?)ScoreScale.ToDisplay(scoreRaw, ScoreFormat.Point10);
        ShikimoriUserRate? acknowledged = await api.UpdateAnimeReceipt(alternativeId, shikimoriStatus, numberOfWatchedEpisodes, numberOfTimesRewatched, score);
        if (acknowledged == null)
            return null;
        return new UpdateAnimeStatusResponse {
            UsesAcknowledgementFields = true,
            AcknowledgedProgress = numberOfWatchedEpisodes,
            AcknowledgedStatus = status,
            AcknowledgedScoreRaw = score != null && acknowledged.Score is >= 0 and <= 10 ? acknowledged.Score * 10 : null
        };
    }
}
