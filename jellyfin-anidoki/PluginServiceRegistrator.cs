using System;
using System.IO;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // The ID lookups read the plugin settings on every use, so a changed ARM server or save location applies at once.
        serviceCollection.AddSingleton(provider => new Helpers.ArmClient(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<Helpers.ArmClient>>(), () => Plugin.Instance?.PluginConfiguration));
        serviceCollection.AddSingleton(provider => new Helpers.AnimeListSource(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IApplicationPaths>(), provider.GetRequiredService<ILogger<Helpers.AnimeListSource>>(),
            () => Plugin.Instance?.PluginConfiguration));
        serviceCollection.AddSingleton(provider => provider.GetRequiredService<Helpers.AnimeListSource>().Index);
        serviceCollection.AddSingleton(provider => new Operations.OperationStore(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton<Operations.OperationRunner>();
        // Registered before the triggers so leftovers from the previous run are marked before new work is queued.
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Operations.OperationRunner>());
        serviceCollection.AddSingleton(provider => new Playback.CompletionLedger(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        // After the runner, which marks the operations the last restart cut off as interrupted.
        serviceCollection.AddHostedService<Playback.CompletionRequeue>();
        serviceCollection.AddSingleton(provider => new Matching.MatchStore(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        serviceCollection.AddHostedService<Matching.MatchSweepService>();
        serviceCollection.AddSingleton(provider => new Matching.ResolutionCheckStore(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath)));
        serviceCollection.AddSingleton(provider => new Preview.PreviewStore(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton<Matching.ISeasonWatchState, Matching.JellyfinSeasonWatchState>();
        serviceCollection.AddSingleton<Matching.IMatchLibrary, Matching.JellyfinMatchLibrary>();
        serviceCollection.AddSingleton<Matching.IMatchTrackers, Matching.MatchTrackers>();
        serviceCollection.AddSingleton<Matching.IAutomaticMatches, Matching.AutomaticMatches>();
        serviceCollection.AddSingleton<Matching.IMatchProblems, Matching.RecentMatchProblems>();
        serviceCollection.AddSingleton<Matching.MatchService>();
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
        serviceCollection.AddHostedService<Playback.PlaybackListener>();
        serviceCollection.AddHostedService<UserDataServerEntry>();
    }
}
