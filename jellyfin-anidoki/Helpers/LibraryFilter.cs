using System;
using System.Linq;
using jellyfin_anidoki.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

public static class LibraryFilter {
    /// <summary>
    /// Checks if the supplied item is in a folder the user wants to track for anime updates.
    /// </summary>
    public static bool Includes(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) {
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
