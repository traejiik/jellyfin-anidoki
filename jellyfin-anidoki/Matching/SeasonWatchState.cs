using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Resolution;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Matching;

/// <summary>One watched episode of a source. A movie is episode <see cref="SyncSource.MovieProgress"/>.</summary>
public sealed record WatchedEpisode(int Number, DateTime? LastPlayed);

/// <summary>What Jellyfin says this user has watched of a source. The boundary between pure match code and Jellyfin.</summary>
public interface ISeasonWatchState {
    IReadOnlyList<WatchedEpisode> Watched(SyncSource source, Guid userId);
}

public sealed class JellyfinSeasonWatchState(IUserManager users, IUserDataManager userData) : ISeasonWatchState {
    public IReadOnlyList<WatchedEpisode> Watched(SyncSource source, Guid userId) {
        var user = users.GetUserById(userId);
        if (user == null)
            return [];
        if (source.Item is not Episode episode) {
            var movie = userData.GetUserData(user, source.Item);
            return movie is { Played: true } ? [new WatchedEpisode(SyncSource.MovieProgress, movie.LastPlayedDate)] : [];
        }

        var watched = new List<WatchedEpisode>();
        foreach (Episode sibling in episode.Season?.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>() ?? []) {
            if (sibling.IndexNumber is int number && userData.GetUserData(user, sibling) is { Played: true } data)
                watched.Add(new WatchedEpisode(number, data.LastPlayedDate));
        }
        return watched;
    }
}
