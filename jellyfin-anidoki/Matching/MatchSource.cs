using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace jellyfin_anidoki.Matching;

public enum MatchSourceKind { Season, Movie }

/// <summary>A season or movie that has, or had, a saved match.</summary>
/// <param name="Fingerprint">The item's provider IDs when last seen. It spots a match orphaned by a library rebuild, and never resolves one.</param>
public sealed record MatchSource(string SourceKey, MatchSourceKind Kind, string Title, IReadOnlyDictionary<string, string> Fingerprint) {
    private static readonly HashSet<string> FingerprintIds = new(StringComparer.OrdinalIgnoreCase) { "AniDB", "Tvdb", "Tmdb", "AniList", "Shoko Series" };

    public static MatchSource From(SyncSource source) {
        var fingerprint = new Dictionary<string, string>();
        if (source.Kind == MediaKind.Movie) {
            Add(fingerprint, "movie", source.Item);
            return new MatchSource(source.SourceKey, MatchSourceKind.Movie, source.SearchTitle, fingerprint);
        }
        var episode = (Episode)source.Item;
        Add(fingerprint, "series", episode.Series);
        Add(fingerprint, "season", episode.Season);
        return new MatchSource(source.SourceKey, MatchSourceKind.Season, $"{source.SearchTitle} · season {source.SeasonNumber}", fingerprint);
    }

    private static void Add(Dictionary<string, string> fingerprint, string level, BaseItem? item) {
        if (item?.ProviderIds == null)
            return;
        foreach (var (name, value) in item.ProviderIds.Where(id => FingerprintIds.Contains(id.Key)))
            fingerprint[$"{level}:{name}"] = value;
    }
}
