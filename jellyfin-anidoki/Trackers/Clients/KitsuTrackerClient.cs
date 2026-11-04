using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Api.Kitsu;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Kitsu;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

public sealed class KitsuTrackerClient(KitsuApiCalls api) : ITrackerClient, ITrackerListSource {
    public async Task<List<Anime>?> SearchAnime(string query) => ConvertSearchResults(await api.SearchAnime(query));

    internal static List<Anime> ConvertSearchResults(List<KitsuSearch.KitsuAnime>? animeList) {
        var converted = new List<Anime>();
        if (animeList != null) {
            foreach (KitsuSearch.KitsuAnime anime in animeList)
                converted.Add(ClassConversions.ConvertKitsuAnime(anime));
        }

        return converted;
    }

    public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) {
        KitsuGet.KitsuGetAnime anime = await api.GetAnime(id);
        if (anime == null)
            return null;
        Anime converted = ClassConversions.ConvertKitsuAnime(anime.KitsuAnimeData);
        converted.MyListStatus = await GetConvertedUserList(id);
        return converted;
    }

    /// <summary>The user's list status for an anime, or an empty status when it is not on the list.</summary>
    internal async Task<MyListStatus> GetConvertedUserList(int id) {
        int? userId = await api.GetUserId();
        var userList = new MyListStatus();
        if (userId == null)
            return userList;
        KitsuUpdate.KitsuLibraryEntry entry = await api.GetUserAnimeStatus(userId.Value, id);
        if (entry is not { Attributes: { } })
            return userList;
        userList = new MyListStatus {
            NumEpisodesWatched = entry.Attributes.Progress ?? 0,
            IsRewatching = entry.Attributes.Reconsuming ?? false,
            RewatchCount = entry.Attributes.ReconsumeCount ?? 0
        };
        switch (entry.Attributes.Status) {
            case KitsuUpdate.Status.completed:
                userList.Status = Status.Completed;
                break;
            case KitsuUpdate.Status.current:
                userList.Status = Status.Watching;
                break;
            case KitsuUpdate.Status.dropped:
                userList.Status = Status.Dropped;
                break;
            case KitsuUpdate.Status.on_hold:
                userList.Status = Status.On_hold;
                break;
            case KitsuUpdate.Status.planned:
                userList.Status = Status.Plan_to_watch;
                break;
        }

        return userList;
    }

    public Task<Anime?> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        Task.FromResult<Anime?>(null);

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null) {
        KitsuUpdate.Status kitsuStatus = status switch {
            Status.Watching => KitsuUpdate.Status.current,
            Status.Completed or Status.Rewatching => isRewatching == true ? KitsuUpdate.Status.current : KitsuUpdate.Status.completed,
            Status.On_hold => KitsuUpdate.Status.on_hold,
            Status.Dropped => KitsuUpdate.Status.dropped,
            Status.Plan_to_watch => KitsuUpdate.Status.planned,
            _ => KitsuUpdate.Status.current
        };
        if (!await api.UpdateAnimeStatus(animeId, numberOfWatchedEpisodes, kitsuStatus, isRewatching, numberOfTimesRewatched, startDate, endDate))
            return null;
        return new UpdateAnimeStatusResponse {
            UsesAcknowledgementFields = true,
            AcknowledgedProgress = numberOfWatchedEpisodes,
            AcknowledgedStatus = kitsuStatus == KitsuUpdate.Status.current ? Status.Watching : status == Status.Rewatching ? Status.Completed : status
        };
    }

    public async Task<MalApiCalls.User?> GetUser() {
        int? user = await api.GetUserId();
        return user == null ? null : new MalApiCalls.User { Id = user.Value };
    }

    public async Task<List<Anime>?> GetAnimeList(Status status, int? userId = null) {
        if (userId == null)
            return null;
        KitsuUpdate.Status kitsuStatus = status switch {
            Status.Watching or Status.Rewatching => KitsuUpdate.Status.current,
            Status.Completed => KitsuUpdate.Status.completed,
            Status.On_hold => KitsuUpdate.Status.on_hold,
            Status.Dropped => KitsuUpdate.Status.dropped,
            Status.Plan_to_watch => KitsuUpdate.Status.planned,
            _ => KitsuUpdate.Status.current
        };
        KitsuUpdate.KitsuLibraryEntryListRoot animeList = await api.GetUserAnimeList(userId.Value, status: kitsuStatus);
        if (animeList == null)
            return null;
        var converted = new List<Anime>();
        foreach (KitsuUpdate.KitsuLibraryEntry entry in animeList.Data!) {
            var anime = new Anime();
            if (entry.Relationships?.AnimeData?.Anime is { } related)
                anime.Id = related.Id;
            anime.MyListStatus = new MyListStatus();
            if (entry.Attributes != null) {
                anime.MyListStatus.FinishDate = entry.Attributes.FinishedAt.ToString();
                if (entry.Attributes.Progress != null)
                    anime.MyListStatus.NumEpisodesWatched = entry.Attributes.Progress.Value;
            }

            converted.Add(anime);
        }

        return converted;
    }
}
