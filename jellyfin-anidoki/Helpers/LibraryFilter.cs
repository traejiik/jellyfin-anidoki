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
            LibraryScope scope = ScopeIn(animeLibraries, LibrariesOf(libraryManager, item),
                () => HasAnimeId(item, tmdbId => AnimeListIndex.Shared.AniDbIdForMovie(tmdbId) != null));
            if (scope == LibraryScope.Excluded)
                logger.LogInformation("Item is not in an anime library, or has no anime ID in a Mixed library; ignoring");
            return scope;
        } catch (Exception e) {
            logger.LogInformation("Library check ran into an issue: {ErrorMessage}", e.Message);
            return LibraryScope.Excluded;
        }
    }

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
    /// Whether the item, or the series and season it belongs to, carries an anime provider ID; or, for a movie,
    /// whether <paramref name="isAnimeMovie"/> knows its TMDB ID. Series TMDB IDs are a separate numbering and never count.
    /// </summary>
    internal static bool HasAnimeId(BaseItem item, Func<string, bool>? isAnimeMovie = null) {
        BaseItem?[] carriers = item switch {
            Episode episode => [episode.Series, episode.Season],
            Season season => [season.Series, season],
            _ => [item]
        };
        if (carriers.Any(carrier => carrier?.ProviderIds?.Any(pair =>
                AnimeIdKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value)) == true))
            return true;
        return item is Movie movie && isAnimeMovie != null
            && AnimeListHelpers.TryGetProviderId(movie.ProviderIds, "Tmdb", out string tmdbId) && isAnimeMovie(tmdbId);
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

            logger.LogInformation("Item is in a folder the user does not want to be monitored; ignoring");
            return false;
        } catch (Exception e) {
            logger.LogInformation("Library check ran into an issue: {ErrorMessage}", e.Message);
            return false;
        }
    }
}
