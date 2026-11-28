using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers.Mal;

namespace jellyfin_anidoki.Trackers.AniList;

public sealed class AniListTrackerClient(AniListApiCalls api) : ITrackerClient, ITrackerListSource, ITrackerLibraryClient, IScoreFormatSource {
    public async Task<List<Anime>?> SearchAnime(string query) {
        bool updateNsfw = Plugin.Instance?.PluginConfiguration?.updateNsfw == true;
        return ConvertSearchResults(await api.SearchAnime(query), updateNsfw);
    }

    internal static List<Anime> ConvertSearchResults(List<AniListSearch.Media>? animeList, bool updateNsfw) {
        var converted = new List<Anime>();
        if (animeList == null)
            return converted;
        foreach (AniListSearch.Media media in animeList) {
            if (!updateNsfw && media.IsAdult)
                continue; // Skip NSFW anime if the user doesn't want to update them
            var synonyms = new[] { media.Title?.Romaji, media.Title?.UserPreferred }.Concat(media.Synonyms ?? []).OfType<string>().ToList();
            var anime = new Anime {
                Id = media.Id,
                Title = media.Title?.English,
                AlternativeTitles = new AlternativeTitles {
                    En = media.Title?.English,
                    Ja = media.Title?.Native,
                    Synonyms = synonyms
                },
                NumEpisodes = media.Episodes ?? 0,
            };

            switch (media.Status) {
                case AniListSearch.AiringStatus.FINISHED:
                    anime.Status = AiringStatus.finished_airing;
                    break;
                case AniListSearch.AiringStatus.RELEASING:
                    anime.Status = AiringStatus.currently_airing;
                    break;
                case AniListSearch.AiringStatus.NOT_YET_RELEASED:
                case AniListSearch.AiringStatus.CANCELLED:
                case AniListSearch.AiringStatus.HIATUS:
                    anime.Status = AiringStatus.not_yet_aired;
                    break;
            }

            converted.Add(anime);
        }

        return converted;
    }

    public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false) {
        AniListSearch.Media? anime = await api.GetAnime(id);
        return anime == null ? null : ClassConversions.ConvertAniListAnime(anime);
    }

    public Task<Anime?> GetAnime(ArmClient.OfflineDatabaseResponse ids, string title, bool getRelated = false) =>
        Task.FromResult<Anime?>(null);

    public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status, bool? isRewatching = null,
        int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
        ArmClient.OfflineDatabaseResponse? ids = null, bool? isShow = null, int? scoreRaw = null, bool clearScore = false) {
        AniListSearch.MediaListStatus aniListStatus = status switch {
            Status.Watching => AniListSearch.MediaListStatus.Current,
            Status.Completed => isRewatching == true ? AniListSearch.MediaListStatus.Repeating : AniListSearch.MediaListStatus.Completed,
            Status.On_hold => AniListSearch.MediaListStatus.Paused,
            Status.Dropped => AniListSearch.MediaListStatus.Dropped,
            Status.Plan_to_watch => AniListSearch.MediaListStatus.Planning,
            Status.Rewatching => AniListSearch.MediaListStatus.Repeating,
            _ => AniListSearch.MediaListStatus.Current
        };
        return await api.UpdateAnimeReceipt(animeId, aniListStatus, numberOfWatchedEpisodes, numberOfTimesRewatched, startDate, endDate,
            clearScore ? 0 : scoreRaw);
    }

    public Task<ScoreFormat?> GetScoreFormat() => api.GetScoreFormat();

    public async Task<MalApiCalls.User?> GetUser() {
        AniListViewer.Viewer? user = await api.GetCurrentUser();
        return user == null ? null : ClassConversions.ConvertUser(user.Id, user.Name);
    }

    public async Task<List<Anime>?> GetAnimeList(Status status, int? userId = null) {
        if (userId == null)
            return null;
        AniListSearch.MediaListStatus aniListStatus = status switch {
            Status.Watching => AniListSearch.MediaListStatus.Current,
            Status.Completed => AniListSearch.MediaListStatus.Completed,
            Status.Rewatching => AniListSearch.MediaListStatus.Repeating,
            Status.On_hold => AniListSearch.MediaListStatus.Paused,
            Status.Dropped => AniListSearch.MediaListStatus.Dropped,
            Status.Plan_to_watch => AniListSearch.MediaListStatus.Planning,
            _ => AniListSearch.MediaListStatus.Current
        };
        return ConvertList(await api.GetAnimeList(userId.Value, aniListStatus), status);
    }

    async Task<List<Anime>?> ITrackerLibraryClient.ReadWholeList(CancellationToken cancellationToken) {
        if (await api.GetCurrentUser() is not { Id: > 0 } user)
            return null;
        return await api.GetWholeAnimeList(user.Id, cancellationToken);
    }

    /// <summary>The list as read for one status: each entry carries that status, since the list query filters by it.</summary>
    internal static List<Anime> ConvertList(List<AniListMediaList.Entries>? animeList, Status status) {
        var converted = new List<Anime>();
        if (animeList == null)
            return converted;
        foreach (var media in animeList) {
            if (media.Media?.SiteUrl is not { } siteUrl)
                continue;
            int lastIndex = siteUrl.LastIndexOf("/", StringComparison.CurrentCulture);
            if (lastIndex == -1)
                continue;
            var finishDate = new DateTime();
            if (media.CompletedAt is { Year: { }, Month: { }, Day: { } })
                finishDate = new DateTime(media.CompletedAt.Year.Value, media.CompletedAt.Month.Value, media.CompletedAt.Day.Value);
            converted.Add(new Anime {
                Id = media.Media.Id,
                Title = media.Media.Title?.English ?? media.Media.Title?.Romaji,
                MyListStatus = new MyListStatus {
                    Status = status,
                    FinishDate = finishDate.ToShortDateString(),
                    NumEpisodesWatched = media.Progress ?? -1
                }
            });
        }

        return converted;
    }
}
