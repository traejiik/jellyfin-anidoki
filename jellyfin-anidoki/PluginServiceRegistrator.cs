using System;
using System.IO;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace jellyfin_anidoki;

public class PluginServiceRegistrator : IPluginServiceRegistrator {
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) {
        serviceCollection.AddHttpClient("AniDokiDiscord", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordSettingsService>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordWebhookSender>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordPosterProvider>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordNotificationDispatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Notifications.Discord.DiscordNotificationDispatcher>());
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ManualMarks.ManualMarkStore>();
        serviceCollection.AddSingleton<ManualMarks.ManualMarkQueue>();
        serviceCollection.AddSingleton(provider => {
            string dataPath = provider.GetRequiredService<IApplicationPaths>().DataPath;
            return new ManualMarks.ManualMarkCapabilityRegistry(Storage.AniDokiDatabase.ForDataPath(dataPath),
                Path.Combine(dataPath, "AniDoki", "manual-capabilities.json"));
        });
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
