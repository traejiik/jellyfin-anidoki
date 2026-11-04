using System.Collections.Generic;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Trackers;

/// <summary>What tracker → Jellyfin sync reads. Only MyAnimeList, AniList and Kitsu support it.</summary>
public interface ITrackerListSource {
    Task<MalApiCalls.User?> GetUser();

    Task<List<Anime>?> GetAnimeList(Status status, int? userId = null);
}
