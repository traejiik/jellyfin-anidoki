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
        foreach (Series series in SyncHelper.GetUsersJellyfinLibrary(userId, userManager, libraryManager, logger).OfType<Series>()) {
            logger.LogDebug("Reading the latest watched episodes of {SeriesName}", series.Name);
            var latest = OfSeries(series, userId, userManager, logger);
            if (latest == null) {
                logger.LogWarning("User {UserId} was not found in Jellyfin; {SeriesName} was skipped", userId, series.Name);
                continue;
            }
            targets.AddRange(latest);
        }
        return targets;
    }

    internal static List<Episode>? OfSeries(Series series, Guid userId, IUserManager userManager, ILogger logger) {
        var latestEpisodes = new List<Episode>();

        var seasons = series.Children.OfType<Season>().ToList();
        logger.LogDebug("{SeriesName} has {Count} seasons", series.Name, seasons.Count);
        var user = userManager.GetUserById(userId);
        if (user == null)
            return null;
        foreach (Season season in seasons) {
            logger.LogDebug("Reading user data for {SeriesName} {SeasonName}", series.Name, season.Name);
            if (season.IndexNumber == null) {
                logger.LogDebug("Skipping {SeriesName} {SeasonName}; the season has no number", series.Name, season.Name);
                continue;
            }

            IEnumerable<Episode> episodes = season.GetEpisodes(user, new DtoOptions(), false).OfType<Episode>().ToArray();
            if (!episodes.Any()) {
                logger.LogDebug("{SeriesName} {SeasonName} has no episodes this user can see", series.Name, season.Name);
            }
            logger.LogDebug("{SeriesName} {SeasonName} has {Count} episodes", series.Name, season.Name, season.Children.OfType<Episode>().Count());
            Episode? latestWatchedEpisode;

            try {
                latestWatchedEpisode = episodes.Where(episode => episode.UserData.Any(userData => userData.UserId == user.Id && userData.Played)).MaxBy(episode => episode.IndexNumber);
            } catch (Exception e) {
                logger.LogError(e, "Could not read the watched episodes of {SeriesName} {SeasonName}", series.Name, season.Name);
                continue;
            }

            if (latestWatchedEpisode == null) {
                logger.LogDebug("{SeriesName} {SeasonName} has no watched episodes", series.Name, season.Name);
                continue;
            }
            logger.LogDebug("The latest watched episode of {SeriesName} {SeasonName} is {EpisodeNumber}", series.Name, season.Name, latestWatchedEpisode.IndexNumber);
            latestEpisodes.Add(latestWatchedEpisode);
        }

        logger.LogDebug("{SeriesName} has {Count} seasons with watched episodes", series.Name, latestEpisodes.Count);
        return latestEpisodes;
    }
}
