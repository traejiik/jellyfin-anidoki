using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

/// <summary>Removes the overrides of users deleted while the plugin was not told: it has no user-deletion hook.</summary>
public sealed class MatchSweepService(MatchStore store, IUserManager userManager, ILogger<MatchSweepService> logger) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        try {
            int removed = store.SweepDeletedUsers(userManager.GetUsers().Select(user => user.Id).ToList());
            if (removed > 0)
                logger.LogInformation("Removed {OverrideCount} match overrides of deleted users", removed);
        } catch (Exception error) {
            // An exception leaving a hosted service's StartAsync stops Jellyfin from starting, and no cleanup is worth that.
            logger.LogWarning("Could not check match overrides for deleted users: {ErrorMessage}", error.Message);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
