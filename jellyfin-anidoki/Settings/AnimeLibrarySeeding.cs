using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Settings;

/// <summary>One Jellyfin library as seeding sees it: its ID and collection type (null for mixed content).</summary>
public sealed record SeedLibrary(Guid Id, string? Type);

/// <summary>What seeding did: how many libraries it set, and from how many users.</summary>
public sealed record SeedResult(bool Changed, int Libraries, int Users);

/// <summary>
/// D9: on the first start of 2.0.0, fills the admin's anime libraries from the libraries each user had picked, then
/// drops those per-user choices. Runs once (<see cref="PluginConfiguration.AnimeLibrariesSeeded"/>).
/// </summary>
public sealed class AnimeLibrarySeeding(ILibraryManager libraryManager, IUserManager userManager, ILogger<AnimeLibrarySeeding> logger) : IHostedService {
    /// <summary>Only libraries that can hold shows or movies are seeded, as the admin page lists them.</summary>
    private static readonly string[] SeededTypes = ["tvshows", "movies", "mixed"];

    public Task StartAsync(CancellationToken cancellationToken) {
        var plugin = Plugin.Instance;
        if (plugin?.PluginConfiguration is not { } configuration)
            return Task.CompletedTask;
        try {
            var libraries = libraryManager.GetVirtualFolders()
                .Where(folder => Guid.TryParse(folder.ItemId, out _))
                .Select(folder => new SeedLibrary(Guid.Parse(folder.ItemId), folder.CollectionType?.ToString()))
                .ToList();
            SeedResult result;
            lock (configuration) {
                result = Seed(configuration, libraries, id => userManager.GetUserById(id)?.Username);
                if (result.Changed)
                    plugin.SaveConfiguration();
            }
            if (result.Libraries > 0)
                logger.LogInformation("Set the anime libraries from the users' earlier choices ({Count} libraries, {UserCount} users); the admin page asks to review them",
                    result.Libraries, result.Users);
        } catch (Exception error) {
            logger.LogError(error, "Could not set the anime libraries from the users' earlier choices");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Seeds <paramref name="configuration"/> once. Every library a user with a linked tracker had picked becomes Dedicated;
    /// a user who had every library (no pick) makes every show, movie and mixed library Dedicated. An admin list that is
    /// already set is left alone. With no libraries listed yet it waits for the next start.
    /// </summary>
    /// <param name="userName">A user's Jellyfin name, or null for a user who no longer exists (left out).</param>
    public static SeedResult Seed(PluginConfiguration configuration, IReadOnlyList<SeedLibrary> libraries, Func<Guid, string?> userName) {
        if (configuration.AnimeLibrariesSeeded)
            return new SeedResult(false, 0, 0);
        if (configuration.AnimeLibraries.Length == 0 && libraries.Count == 0)
            return new SeedResult(false, 0, 0);
        var chosen = new Dictionary<Guid, List<string>>();
        int users = 0;
        if (configuration.AnimeLibraries.Length == 0) {
            var eligible = libraries.Where(library => library.Type == null || SeededTypes.Contains(library.Type, StringComparer.OrdinalIgnoreCase))
                .Select(library => library.Id).ToList();
            foreach (var user in configuration.UserConfig ?? []) {
                if (user.UserApiAuth?.Any(auth => !string.IsNullOrEmpty(auth.AccessToken)) != true || userName(user.UserId) is not string name)
                    continue;
                var picked = user.LibraryToCheck is null or { Length: 0 }
                    ? eligible
                    : eligible.Where(id => user.LibraryToCheck.Any(value => Guid.TryParse(value, out Guid pick) && pick == id)).ToList();
                foreach (Guid id in picked) {
                    if (!chosen.TryGetValue(id, out var names))
                        chosen[id] = names = [];
                    names.Add(name);
                }
                users++;
            }
            // Library order as Jellyfin lists it, so the admin page reads the same.
            var order = eligible.Where(chosen.ContainsKey).ToList();
            configuration.AnimeLibraries = order.Select(id => new AnimeLibrary { LibraryId = id, Mode = AnimeLibraryMode.Dedicated }).ToArray();
            configuration.AnimeLibrarySeeds = order.Select(id => new AnimeLibrarySeed { LibraryId = id, ChosenBy = chosen[id].Distinct().ToArray() }).ToArray();
        }
        configuration.AnimeLibrariesSeeded = true;
        foreach (var user in configuration.UserConfig ?? [])
            user.LibraryToCheck = null;
        return new SeedResult(true, chosen.Count, chosen.Count > 0 ? users : 0);
    }
}
