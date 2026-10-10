using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace jellyfin_anidoki;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ManualMarks.ManualMarkStore>();
        serviceCollection.AddSingleton<ManualMarks.ManualMarkQueue>();
        serviceCollection.AddSingleton(provider => new ManualMarks.ManualMarkCapabilityRegistry(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "AniDoki", "manual-capabilities.json")));
        serviceCollection.AddSingleton<ManualMarks.ManualMarkService>();
        serviceCollection.AddHostedService<ManualMarks.ManualMarkWorker>();
        serviceCollection.AddScoped<ManualMarks.ManualMarkRequestFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ManualMarks.ManualMarkRequestFilter>());
        serviceCollection.AddScoped<Notifications.AuthenticatedSessionResolver>();
        serviceCollection.AddSingleton<Notifications.NotificationEventStore>();
        serviceCollection.AddHostedService<Notifications.NotificationDeliveryService>();
        serviceCollection.AddHostedService<SessionServerEntry>();
        serviceCollection.AddHostedService<UserDataServerEntry>();
    }
}
