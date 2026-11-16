using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

/// <summary>Whether an item is synced, and how it may be matched.</summary>
public enum LibraryScope {
    /// <summary>Not synced.</summary>
    Excluded,

    /// <summary>Synced and matched normally.</summary>
    Included,

    /// <summary>Synced only through IDs or a saved match, never through title search: a Mixed library.</summary>
    IdsOnly
}

public static class LibraryFilter {
    /// <summary>Provider ID keys that mark a title in a Mixed library as anime, compared ignoring case.</summary>
    private static readonly string[] AnimeIdKeys = ["AniList", "AniDB", "MyAnimeList", "Kitsu"];

    /// <summary>Whether the item is synced at all. See <see cref="Scope(UserConfig, ILibraryManager, ILogger, BaseItem)"/>.</summary>
    public static bool Includes(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) =>
        Scope(userConfig, libraryManager, logger, item) != LibraryScope.Excluded;

    /// <summary>
    /// Once the admin has chosen any anime library, that list decides. Until then each user's
    /// <see cref="UserConfig.LibraryToCheck"/> does, exactly as before 1.6.0.
    /// </summary>
    public static LibraryScope Scope(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) =>
        Scope(Plugin.Instance?.PluginConfiguration.AnimeLibraries, userConfig, libraryManager, logger, item);

    internal static LibraryScope Scope(IReadOnlyCollection<AnimeLibrary>? animeLibraries, UserConfig userConfig,
        ILibraryManager libraryManager, ILogger logger, BaseItem item) {
        if (animeLibraries is null or { Count: 0 })
            return UserIncludes(userConfig, libraryManager, logger, item) ? LibraryScope.Included : LibraryScope.Excluded;
        try {
            LibraryScope scope = AnimeScope(animeLibraries, libraryManager, item);
            if (scope == LibraryScope.Excluded)
                logger.LogDebug("{ItemName} is not in an anime library, or has no anime ID in a Mixed library; ignoring it", item.Name);
            return scope;
        } catch (Exception e) {
            logger.LogWarning(e, "Could not check which library {ItemName} is in; ignoring it", item.Name);
            return LibraryScope.Excluded;
        }
    }

    /// <summary>
    /// Whether the anime libraries sync the item, without logging: for listing many items at once. Throws as the
    /// library lookup does; a caller that cannot afford that catches it.
    /// </summary>
    public static bool InAnimeLibraries(IReadOnlyCollection<AnimeLibrary> animeLibraries, ILibraryManager libraryManager, BaseItem item) =>
        AnimeScope(animeLibraries, libraryManager, item) != LibraryScope.Excluded;

    private static LibraryScope AnimeScope(IReadOnlyCollection<AnimeLibrary> animeLibraries, ILibraryManager libraryManager, BaseItem item) =>
        ScopeIn(animeLibraries, LibrariesOf(libraryManager, item), () => HasAnimeId(item, AnimeListIndex.Shared));

    /// <summary>The most permissive mode among the item's libraries. A library with no entry counts as Off.</summary>
    internal static LibraryScope ScopeIn(IReadOnlyCollection<AnimeLibrary> animeLibraries, IEnumerable<Guid> itemLibraries, Func<bool> hasAnimeId) {
        var modes = itemLibraries.Select(id => animeLibraries.FirstOrDefault(entry => entry.LibraryId == id)?.Mode).ToList();
        if (modes.Contains(AnimeLibraryMode.Dedicated))
            return LibraryScope.Included;
        if (modes.Contains(AnimeLibraryMode.Mixed) && hasAnimeId())
            return LibraryScope.IdsOnly;
        return LibraryScope.Excluded;
    }

    /// <summary>The libraries whose folders hold the item's top-level folder: the same lookup the per-user filter makes.</summary>
    internal static IReadOnlyList<Guid> LibrariesOf(ILibraryManager libraryManager, BaseItem item) {
        string? path = item.GetTopParent()?.Path;
        if (path == null)
            return [];
        return libraryManager.GetVirtualFolders()
            .Where(folder => folder.Locations.Contains(path))
            .Select(folder => Guid.TryParse(folder.ItemId, out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    /// <summary>
    /// Whether the item, or the series and season it belongs to, carries an anime provider ID. A movie's AniDB ID counts
    /// only when <paramref name="animeList"/> ties it to the movie (<see cref="MovieAniDbCheck.Confirms"/>), because
    /// Jellyfin's AniDB plugin can give a Western film an anime's ID; a movie with no other anime ID also counts when the
    /// list maps its TMDB ID. Series TMDB IDs are a separate numbering and never count.
    /// </summary>
    internal static bool HasAnimeId(BaseItem item, AnimeListIndex? animeList = null) {
        BaseItem?[] carriers = item switch {
            Episode episode => [episode.Series, episode.Season],
            Season season => [season.Series, season],
            _ => [item]
        };
        bool movie = item is Movie;
        if (carriers.Any(carrier => carrier?.ProviderIds?.Any(pair =>
                AnimeIdKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value)
                && !(movie && pair.Key.Equals("AniDB", StringComparison.OrdinalIgnoreCase))) == true))
            return true;
        if (item is not Movie film || animeList == null)
            return false;
        if (MovieAniDbCheck.For(film, animeList) is { } check)
            return check.Confirms;
        return AnimeListHelpers.TryGetProviderId(film.ProviderIds, "Tmdb", out string tmdbId) && animeList.AniDbIdForMovie(tmdbId) != null;
    }

    /// <summary>The per-user filter used before 1.6.0, unchanged. Removed with LibraryToCheck in 2.0.0.</summary>
    private static bool UserIncludes(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) {
        try {
            // user has no library filters; a missing list (an older configuration) means the same
            if (userConfig.LibraryToCheck is null or { Length: 0 }) {
                return true;
            }

            // item is in a path of a folder the user wants to be monitored
            var topParent = item.GetTopParent();
            if (topParent is not null) {
                var allLocations = libraryManager.GetVirtualFolders()
                    .Where(folder => userConfig.LibraryToCheck.Contains(folder.ItemId))
                    .SelectMany(folder => folder.Locations)
                    .ToHashSet();
                if (allLocations.Contains(topParent.Path)) {
                    return true;
                }
            }

            logger.LogDebug("{ItemName} is not in a library this user syncs; ignoring it", item.Name);
            return false;
        } catch (Exception e) {
            logger.LogWarning(e, "Could not check which library {ItemName} is in; ignoring it", item.Name);
            return false;
        }
    }
}
