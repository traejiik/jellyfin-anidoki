using System;
using System.IO;
using System.Net.Http;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki.Settings;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

public class PluginServiceRegistrator : IPluginServiceRegistrator {
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) {
        // First, so every AniDoki logger created from here on also writes to the AniDoki log file.
        Logging.AniDokiLogFile.Attach();
        serviceCollection.AddHttpClient("AniDokiDiscord", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordSettingsService>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordWebhookSender>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordPosterProvider>();
        serviceCollection.AddSingleton<Notifications.Discord.DiscordNotificationDispatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Notifications.Discord.DiscordNotificationDispatcher>());
        serviceCollection.AddSingleton(TimeProvider.System);
        // The ID lookups read the plugin settings on every use, so a changed ARM server or save location applies at once.
        serviceCollection.AddSingleton(provider => new AnimeIds.ArmClient(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<AnimeIds.ArmClient>>(), () => Plugin.Instance?.PluginConfiguration));
        serviceCollection.AddSingleton(provider => new AnimeIds.AnimeListSource(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IApplicationPaths>(), provider.GetRequiredService<ILogger<AnimeIds.AnimeListSource>>(),
            () => Plugin.Instance?.PluginConfiguration));
        serviceCollection.AddSingleton(provider => provider.GetRequiredService<AnimeIds.AnimeListSource>().Index);
        serviceCollection.AddSingleton(provider => new Operations.OperationStore(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        // D6: the season key and the rate-limit window come from Jellyfin's library and the trackers' shared limiter.
        serviceCollection.AddSingleton(provider => {
            var library = provider.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
            var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            return new Operations.RetryQueue(Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath),
                provider.GetRequiredService<TimeProvider>(), item => Playback.RetryWorker.SeriesKey(library, item),
                tracker => Auth.MemoryCacheHelper.RateLimitedUntil(tracker, cache));
        });
        serviceCollection.AddSingleton(provider => new Operations.AccountAttention(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath)));
        serviceCollection.AddSingleton(provider => new Operations.HistoryReader(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        // D9: before anything syncs, so the first start of 2.0.0 scans the libraries the users had picked.
        serviceCollection.AddHostedService<Settings.AnimeLibrarySeeding>();
        serviceCollection.AddSingleton<Operations.OperationRunner>();
        // Registered before the triggers so leftovers from the previous run are marked before new work is queued.
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Operations.OperationRunner>());
        serviceCollection.AddSingleton(provider => new Playback.CompletionLedger(
            Storage.AniDokiDatabase.ForDataPath(provider.GetRequiredService<IApplicationPaths>().DataPath), provider.GetRequiredService<TimeProvider>()));
        // After the runner, which marks the operations the last restart cut off as interrupted.
        serviceCollection.AddSingleton<Playback.TriggerReplay>();
        serviceCollection.AddHostedService<Playback.CompletionRequeue>();
        serviceCollection.AddHostedService<Playback.RetryWorker>();
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
        serviceCollection.AddSingleton<Progress.ISeriesItems, Progress.JellyfinSeriesItems>();
        serviceCollection.AddSingleton<Progress.ScoreFormatCache>();
        serviceCollection.AddSingleton<Progress.SeriesProgressReader>();
        serviceCollection.AddSingleton<Progress.ProgressSources>();
        serviceCollection.AddSingleton<Progress.AniListAiring>();
        serviceCollection.AddSingleton<Progress.SeriesDetails>();
        serviceCollection.AddSingleton<Progress.TrackerLibraryCache>();
        serviceCollection.AddSingleton<Progress.LibraryProgress>();
        serviceCollection.AddSingleton<Progress.EntryEditor>();
        serviceCollection.AddSingleton<Progress.IPlayedEpisodes, Progress.JellyfinPlayedEpisodes>();
        serviceCollection.AddSingleton<Progress.SeriesInbound>();
        serviceCollection.AddSingleton<Progress.SeriesOutbound>();
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
        serviceCollection.AddSingleton<Playback.WatchTracker>();
        serviceCollection.AddHostedService<Playback.PlaybackListener>();
        serviceCollection.AddHostedService<UserDataServerEntry>();
    }
}
