using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Resolution;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

/// <summary>A season or movie as the match manager lists it.</summary>
public sealed record LibrarySourceEntry(string SourceKey, MatchSourceKind Kind, string Title);

/// <summary>A season or movie found in the library, with what a match is checked against.</summary>
/// <param name="Last">Its highest-numbered episode, or the movie: what automatic resolution and the watch state read.</param>
/// <param name="Episodes">The episode numbers the library holds, in order; <c>[1]</c> for a movie.</param>
public sealed record LibrarySource(MatchSource Info, SyncSource Last, IReadOnlyList<int> Episodes);

/// <summary>Which seasons and movies exist, and which a user may see. The boundary between the match API and Jellyfin.</summary>
public interface IMatchLibrary {
    /// <param name="viewer">Null for an admin, who sees every library; otherwise only what this user can see and syncs.</param>
    IReadOnlyList<LibrarySourceEntry> List(Guid? viewer);

    /// <returns>The source, or null when it does not exist, has no numbered episode, or <paramref name="viewer"/> cannot see it.</returns>
    LibrarySource? Find(string sourceKey, Guid? viewer);
}

/// <summary>Parses <see cref="SyncSource.SourceKey"/>.</summary>
public static class SourceKeys {
    /// <returns>The series ID and season number, or the movie ID with a null season; null when the key is malformed.</returns>
    public static (Guid ItemId, int? SeasonNumber)? Parse(string sourceKey) {
        string[] parts = sourceKey.Split(':');
        if (parts is ["movie", var movie] && Guid.TryParseExact(movie, "N", out Guid movieId))
            return (movieId, null);
        if (parts is ["season", var series, var season] && Guid.TryParseExact(series, "N", out Guid seriesId)
            && int.TryParse(season, NumberStyles.None, CultureInfo.InvariantCulture, out int seasonNumber))
            return (seriesId, seasonNumber);
        return null;
    }
}

public sealed class JellyfinMatchLibrary(ILibraryManager library, IUserManager users, ILogger<JellyfinMatchLibrary> logger) : IMatchLibrary {
    public IReadOnlyList<LibrarySourceEntry> List(Guid? viewer) {
        User? user = null;
        if (viewer is Guid id && (user = users.GetUserById(id)) == null)
            return [];
        var query = new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], IsVirtualItem = false };
        var sources = new List<LibrarySourceEntry>();
        foreach (BaseItem item in library.GetItemList(query)) {
            if (SyncHelper.MediaShouldBeIgnored(item) || viewer is Guid owner && !Syncs(owner, item))
                continue;
            if (item is Movie movie) {
                sources.Add(new LibrarySourceEntry($"movie:{movie.Id:N}", MatchSourceKind.Movie, movie.Name));
            } else if (item is Series series) {
                foreach (Season season in series.Children.OfType<Season>().Where(season => season.IndexNumber != null))
                    sources.Add(new LibrarySourceEntry($"season:{series.Id:N}:{season.IndexNumber}", MatchSourceKind.Season, $"{series.Name} · season {season.IndexNumber}"));
            }
        }
        return sources;
    }

    public LibrarySource? Find(string sourceKey, Guid? viewer) {
        if (SourceKeys.Parse(sourceKey) is not var (itemId, seasonNumber))
            return null;
        User? user = null;
        if (viewer is Guid id && (user = users.GetUserById(id)) == null)
            return null;
        BaseItem? item = library.GetItemById(itemId);
        if (item == null || SyncHelper.MediaShouldBeIgnored(item) || user != null && (!item.IsVisibleStandalone(user) || !Syncs(user.Id, item)))
            return null;

        if (seasonNumber == null)
            return item is Movie movie && SyncSource.From(movie) is { } source ? new LibrarySource(MatchSource.From(source), source, [SyncSource.MovieProgress]) : null;
        if (item is not Series series || series.Children.OfType<Season>().FirstOrDefault(season => season.IndexNumber == seasonNumber) is not { } found)
            return null;
        var episodes = found.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>()
            .Where(episode => episode.IndexNumber != null && !SyncHelper.MediaShouldBeIgnored(episode))
            .OrderBy(episode => episode.IndexNumber)
            .ToList();
        if (episodes.Count == 0 || SyncSource.From(episodes[^1]) is not { } last)
            return null;
        return new LibrarySource(MatchSource.From(last), last, episodes.Select(episode => episode.IndexNumber!.Value).Distinct().ToList());
    }

    /// <summary>A user's library filter applies; a user who never saved AniDoki settings has none.</summary>
    private bool Syncs(Guid userId, BaseItem item) {
        var config = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(entry => entry.UserId == userId);
        return config == null || LibraryFilter.Includes(config, library, logger, item);
    }
}
