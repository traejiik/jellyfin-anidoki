using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Helpers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

/// <summary>What a sync to trackers sends: the highest-numbered played episode of each season the user can see.</summary>
internal static class LatestWatchedEpisodes {
    public static List<Episode> For(Guid userId, IUserManager userManager, ILibraryManager libraryManager, ILogger logger) {
        var targets = new List<Episode>();
        foreach (Series series in SyncHelper.GetUsersJellyfinLibrary(userId, userManager, libraryManager).OfType<Series>()) {
            logger.LogDebug("(Sync) Retrieved {SeriesName}'s seasons latest watched episode and when it was watched...", series.Name);
            var latest = OfSeries(series, userId, userManager, logger);
            if (latest == null) {
                logger.LogError("(Sync) User with ID of {UserId} not found", userId);
                continue;
            }
            targets.AddRange(latest);
        }
        return targets;
    }

    private static List<Episode>? OfSeries(Series series, Guid userId, IUserManager userManager, ILogger logger) {
        var latestEpisodes = new List<Episode>();

        logger.LogDebug("(Sync) Getting {SeriesName} seasons latest watched episode", series.Name);
        var seasons = series.Children.OfType<Season>().ToList();
        logger.LogDebug("(Sync) Series {SeriesName} contains {SeasonCount} seasons", series.Name, seasons.Count);
        var user = userManager.GetUserById(userId);
        if (user == null)
            return null;
        foreach (Season season in seasons) {
            logger.LogDebug("(Sync) Getting user data for {SeasonName} of {SeriesName}...", season.Name, series.Name);
            if (season.IndexNumber == null) {
                logger.LogError("(Sync) Season index number is null. Skipping...");
                continue;
            }

            IEnumerable<Episode> episodes = season.GetEpisodes(user, new DtoOptions(), false).OfType<Episode>().ToArray();
            if (!episodes.Any()) {
                logger.LogDebug("(Sync) No (user visible) episodes found for {SeasonName} of {SeriesName}", season.Name, series.Name);
            }
            logger.LogDebug("(Sync) Season contains {EpisodeCount} episodes", season.Children.OfType<Episode>().Count());
            Episode? latestWatchedEpisode;

            try {
                latestWatchedEpisode = episodes.Where(episode => episode.UserData.Any(userData => userData.UserId == user.Id && userData.Played)).MaxBy(episode => episode.IndexNumber);
            } catch (Exception e) {
                logger.LogError("(Sync) Could not get user episodes watched for {SeasonName}; error: {ErrorMessage}", season.Name, e.Message);
                continue;
            }

            if (latestWatchedEpisode == null) {
                logger.LogDebug("(Sync) No episodes watched for {SeasonName}", season.Name);
                continue;
            }
            logger.LogDebug("(Sync) The latest watched episode for this user of this season is {EpisodeNumber}", latestWatchedEpisode.IndexNumber);
            latestEpisodes.Add(latestWatchedEpisode);
        }

        logger.LogDebug("(Sync) Found {SeasonCount} seasons that contain user data", latestEpisodes.Count);
        return latestEpisodes;
    }
}
