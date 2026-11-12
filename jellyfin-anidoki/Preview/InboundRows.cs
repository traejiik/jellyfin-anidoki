using System;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Preview;

/// <summary>Turns an inbound plan into preview rows. Reads user data; never saves it.</summary>
internal static class InboundRows {
    public static PreviewRow FromSeason(InboundSeasonPlan plan, ApiName provider, User user, IUserDataManager userData, bool applySavedMatches) {
        Season season = plan.Season;
        bool Played(BaseItem item) => userData.GetUserData(user, item)?.Played == true;
        (PreviewRowKind kind, string reason) = plan.SeasonIgnored
            ? (PreviewRowKind.Skipped, "ignored")
            : plan.Episodes.Any(episode => !Played(episode)) || (plan.MarksSeason && !Played(season))
                ? (PreviewRowKind.Change, "mark-played")
                : (PreviewRowKind.NoChange, "already-played");
        int ignored = plan.IgnoredEpisodes.Count;
        string? note = ignored switch {
            0 => null,
            1 => "1 episode carries the ignore tag, so the season itself is not marked",
            _ => string.Create(CultureInfo.InvariantCulture, $"{ignored} episodes carry the ignore tag, so the season itself is not marked")
        };
        string fingerprint = WatchedFingerprint.Of(season.Children.OfType<Episode>()
            .Where(episode => episode.IndexNumber != null && Played(episode)).Select(episode => episode.IndexNumber!.Value));
        Anime entry = plan.Source.entry;
        return new PreviewRow(0, PreviewDirection.FromTrackers, provider, SourceKey(season), season.Id, Title(season),
            plan.Episodes.Count == 0 ? null : plan.Episodes.Max(episode => episode.IndexNumber), TargetId(entry), entry.Title ?? "",
            entry.MyListStatus?.NumEpisodesWatched, kind, reason, null, note, null, applySavedMatches, fingerprint, null, null,
            kind == PreviewRowKind.Change, kind == PreviewRowKind.Change, null,
            new InboundChange(season.Id, plan.Episodes.Select(episode => episode.Id).ToList(), plan.PlayedDate, plan.MarksSeason),
            Entry(entry, provider));
    }

    /// <summary>A tracker entry that maps to no Jellyfin season, listed under its own title.</summary>
    public static PreviewRow FromUnresolved(InboundUnresolved unresolved, ApiName provider, bool applySavedMatches) {
        Anime entry = unresolved.Entry;
        return new PreviewRow(0, PreviewDirection.FromTrackers, provider, "", Guid.Empty, entry.Title ?? "", null, TargetId(entry),
            entry.Title ?? "", entry.MyListStatus?.NumEpisodesWatched, PreviewRowKind.Unresolved, unresolved.Reason, null, null, null,
            applySavedMatches, null, null, null, false, false, null, null, Entry(entry, provider));
    }

    // The same key outbound rows and saved matches use, so both directions of one season group together.
    private static string SourceKey(Season season) =>
        string.Create(CultureInfo.InvariantCulture, $"season:{season.SeriesId:N}:{season.IndexNumber}");

    private static string Title(Season season) =>
        string.Create(CultureInfo.InvariantCulture, $"{season.SeriesName ?? "Unknown series"} · season {season.IndexNumber}");

    private static string TargetId(Anime entry) => entry.Id.ToString(CultureInfo.InvariantCulture);

    private static InboundEntry Entry(Anime entry, ApiName provider) =>
        new(provider, entry.Id, entry.Title ?? "", entry.MyListStatus?.Status, entry.MyListStatus?.NumEpisodesWatched ?? 0, entry.MyListStatus?.FinishDate);
}
