using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

public sealed class MalTrackerClient(MalApiCalls api) : ITrackerClient, ITrackerListSource {
    public async Task<List<Anime>?> SearchAnime(string query) {
        bool updateNsfw = Plugin.Instance?.PluginConfiguration?.updateNsfw == true;
        return await api.SearchAnime(query, ["id", "title", "alternative_titles", "num_episodes", "status"], updateNsfw);
    }

    public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) =>
        WithScore(await api.GetAnime(id, ["title", "related_anime", "my_list_status", "num_episodes"]));

    public Task<Anime?> GetAnime(ArmClient.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        Task.FromResult<Anime?>(null);

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        ArmClient.OfflineDatabaseResponse? ids = null, bool? isShow = null, int? scoreRaw = null, bool clearScore = false) =>
        await api.UpdateAnimeStatus(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched, startDate, endDate,
            clearScore ? 0 : (int?)ScoreScale.ToDisplay(scoreRaw, ScoreFormat.Point10));

    public async Task<MalApiCalls.User?> GetUser() => await api.GetUserInformation();

    public async Task<List<Anime>?> GetAnimeList(Status status, int? userId = null) {
        var list = await api.GetUserAnimeList(status);
        return list?.Select(entry => WithScore(entry.Anime)).OfType<Anime>().ToList();
    }

    /// <summary>MAL scores 1–10, with 0 for unrated; the contracts carry 10–100 and null.</summary>
    private static Anime? WithScore(Anime? anime) {
        if (anime?.MyListStatus is { } status)
            status.ScoreRaw = status.Score is > 0 and <= 10 ? status.Score * 10 : null;
        return anime;
    }
}
