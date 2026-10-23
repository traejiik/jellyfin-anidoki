using System.Collections.Generic;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace jellyfin_anidoki.Resolution;

/// <summary>
/// What Jellyfin knows about one played item, read once. A movie is episode 1 of season 1, as the anime-list lookup
/// and the tracker writes treat it.
/// </summary>
/// <param name="Item">The library item, for the anime-list lookup that reads its provider IDs.</param>
/// <param name="SearchTitle">What title search sends: the series name, or the movie's name.</param>
/// <param name="CandidateTitles">Every title a search result may match.</param>
/// <param name="FallbackAniListId">The AniList ID on the series (season 1 only) or the movie, when it parses.</param>
public sealed record SyncSource(Video Item, MediaKind Kind, int SeasonNumber, int EpisodeNumber, string SearchTitle,
    IReadOnlyList<string?> CandidateTitles, string? EpisodeName, int? FallbackAniListId) {
    /// <summary>A movie counts as one watched episode. Held here so the library item's own IndexNumber is never changed.</summary>
    public const int MovieProgress = 1;

    /// <returns>The source, or null when an episode lacks its episode or season number.</returns>
    public static SyncSource? From(Video video) {
        switch (video) {
            case Episode { IndexNumber: int episodeNumber, Season.IndexNumber: int seasonNumber } episode:
                return FromEpisode(episode, seasonNumber, episodeNumber);
            case Movie movie:
                return new SyncSource(movie, MediaKind.Movie, 1, MovieProgress, movie.Name, [movie.Name, movie.OriginalTitle], null,
                    ParseAniList(movie.ProviderIds));
            default:
                return null;
        }
    }

    private static SyncSource FromEpisode(Episode episode, int seasonNumber, int episodeNumber) {
        Series? series = episode.Series;
        var titles = new List<string?> { episode.SeriesName };
        if (series?.OriginalTitle != null)
            titles.Add(series.OriginalTitle);
        int? fallback = seasonNumber == 1 ? ParseAniList(series?.ProviderIds) : null;
        return new SyncSource(episode, MediaKind.Series, seasonNumber, episodeNumber, episode.SeriesName, titles, episode.Name, fallback);
    }

    private static int? ParseAniList(Dictionary<string, string>? ids) =>
        ids != null && ids.TryGetValue("AniList", out var value) && int.TryParse(value, out int id) ? id : null;
}
