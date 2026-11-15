using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Notifications;

public sealed class NotificationDeliveryService(NotificationEventStore store, IServerConfigurationManager configuration,
    ILogger<NotificationDeliveryService> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            NotificationWebIntegration.BasePath = configuration.GetNetworkConfiguration().BaseUrl ?? "";
            if (!NotificationWebIntegration.Register())
                logger.LogInformation("AniDoki web notifications need a compatible File Transformation plugin and a web refresh");
        } catch (Exception error) { logger.LogWarning(error, "Could not register AniDoki's notification bootstrap; tracker syncs are unaffected"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) store.Prune(); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken) {
        try { NotificationWebIntegration.Remove(); } catch (Exception error) { logger.LogWarning(error, "Could not remove AniDoki's notification transformation"); }
        await base.StopAsync(cancellationToken);
    }
}
