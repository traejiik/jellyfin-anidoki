using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;
using MediaBrowser.Controller.Entities.TV;

namespace jellyfin_anidoki;

/// <summary>What a Tracker → Jellyfin sync would mark for one Jellyfin season, and the tracker entry it came from.</summary>
/// <param name="Episodes">Numbered episodes up to the tracker's progress (all of them when it has none), without ignored ones.</param>
/// <param name="IgnoredEpisodes">Episodes in range that carry the ignore tag; any of them keeps the season itself unmarked.</param>
/// <param name="SeasonIgnored">The season carries the ignore tag, so nothing in it is marked.</param>
internal sealed record InboundSeasonPlan(Season Season, Sync.SyncAnimeMetadata Source, DateTime PlayedDate, IReadOnlyList<Episode> Episodes,
    IReadOnlyList<Episode> IgnoredEpisodes, bool SeasonIgnored) {
    public bool MarksSeason => !SeasonIgnored && IgnoredEpisodes.Count == 0;
}

/// <summary>A tracker entry that maps to no Jellyfin season. <paramref name="Reason"/> is no-anidb-id or no-season.</summary>
internal sealed record InboundUnresolved(Anime Entry, string Reason);

/// <param name="EntryCount">How many tracker entries were read.</param>
internal sealed record InboundPlanResult(int EntryCount, IReadOnlyList<InboundSeasonPlan> Seasons, IReadOnlyList<InboundUnresolved> Unresolved);

/// <summary>The decisions <see cref="Sync"/> made inline before 1.6.0, per season. Reads the season's tags and children only.</summary>
internal static class InboundPlanner {
    /// <param name="now">The played date when the tracker has no finish date.</param>
    public static InboundSeasonPlan ForSeason(Season season, Sync.SyncAnimeMetadata found, DateTime now) {
        DateTime played = found.completedAt ?? now;
        if (SyncHelper.MediaShouldBeIgnored(season))
            return new InboundSeasonPlan(season, found, played, [], [], true);

        var episodes = new List<Episode>();
        var ignored = new List<Episode>();
        foreach (Episode episode in season.Children.OfType<Episode>()) {
            if (episode.IndexNumber is not int number || (found.episodesWatched != -1 && number > found.episodesWatched))
                continue;
            // Season and series tags were checked before; only the episode's own tag matters here.
            (SyncHelper.HasIgnoreTag(episode) ? ignored : episodes).Add(episode);
        }
        return new InboundSeasonPlan(season, found, played, episodes, ignored, false);
    }
}
