using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

/// <summary>
/// The one place AniDōki marks Jellyfin items played: a sync from trackers, applying a preview from trackers, and the
/// progress popup's Sync to Jellyfin. It only ever sets played, never clears it.
/// </summary>
/// <param name="logger">The caller's logger, so the lines read under the caller's category.</param>
internal sealed class PlayedMarker(IUserDataManager userDataManager, ILogger logger) {
    /// <summary>Marks the episodes played on <paramref name="playedDate"/>, then the season unless <paramref name="marksSeason"/> is false.</summary>
    public void Mark(User user, Season season, IReadOnlyList<Episode> episodes, DateTime playedDate, bool marksSeason) {
        foreach (Episode episode in episodes) {
            logger.LogDebug("Marking {SeriesName} season {SeasonNumber} episode {EpisodeNumber} as played for user {UserId}", episode.Series.Name, episode.Season.IndexNumber, episode.IndexNumber, user.Id);
            MarkItem(user, episode, playedDate);
        }

        if (!marksSeason) {
            logger.LogDebug("Not marking {ItemName} itself as played; one or more of its episodes carry the ignore tag", season.Name);
        } else {
            // this could fail because show has ovas, hard to detect. todo warn users
            MarkItem(user, season, playedDate);
            logger.LogDebug("Marked {ItemName} itself as played", season.Name);
        }
    }

    /// <summary>Marks one item (an episode, season or movie) played on <paramref name="playedDate"/>.</summary>
    public void MarkItem(User user, BaseItem item, DateTime? playedDate) =>
        userDataManager.SaveUserData(user, item, SetUserData(user, item, playedDate), UserDataSaveReason.UpdateUserData, CancellationToken.None);

    private UserItemData SetUserData(User user, BaseItem itemToBeUpdated, DateTime? completedDate) {
        // Jellyfin creates the user data row when it reads one, so a null here is unexpected; it stays an exception, which the operation reports.
        var userItemData = userDataManager.GetUserData(user, itemToBeUpdated)!;
        userItemData.Played = true;
        if (completedDate != null) {
            userItemData.LastPlayedDate = completedDate.Value;
        }

        return userItemData;
    }
}
