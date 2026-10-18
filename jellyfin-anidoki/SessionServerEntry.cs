#nullable disable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki {
    public class SessionServerEntry : IHostedService {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<SessionServerEntry> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;
        private readonly NotificationEventStore _notifications;
        private readonly OperationRunner _runner;
        private bool _started;
        private readonly ManualMarkStore _manualMarks;
        private readonly Notifications.Discord.DiscordNotificationDispatcher _discord;


        private readonly ILibraryManager _libraryManager;

        public SessionServerEntry(ISessionManager sessionManager, ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory, ILibraryManager libraryManager,
            IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor,
            IApplicationPaths applicationPaths, IMemoryCache memoryCache, NotificationEventStore notifications, OperationRunner runner, ManualMarkStore manualMarks = null, Notifications.Discord.DiscordNotificationDispatcher discord = null) {
            _httpClientFactory = httpClientFactory;
            _serverApplicationHost = serverApplicationHost;
            _httpContextAccessor = httpContextAccessor;
            _applicationPaths = applicationPaths;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SessionServerEntry>();
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _memoryCache = memoryCache;
            _delayer = new Delayer();
            _notifications = notifications;
            _runner = runner;
            _manualMarks = manualMarks;
            _discord = discord;
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            if (!_started)
                _sessionManager.PlaybackStopped += PlaybackStopped;
            _started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            if (_started)
                _sessionManager.PlaybackStopped -= PlaybackStopped;
            _started = false;
            return Task.CompletedTask;
        }

        public void PlaybackStopped(object sender, PlaybackStopEventArgs e) {
            try {
                // Unfinished playback never syncs (UpdateProviderStatus requires completion), so it starts no operation.
                if (!e.PlayedToCompletion)
                    return;
                if (SyncHelper.MediaShouldBeIgnored(e.Item)) {
                    _logger.LogDebug("(Sync) Ignoring {ItemName}; ignore tag detected", e.Item.Name);
                    return;
                }
                var context = PlaybackNotificationContext.Capture(e.Item, e.Session);
                BaseItem item = e.Item;
                foreach (User user in e.Users) {
                    Guid userId = user.Id;
                    if (_runner.Enqueue(userId, OperationKind.Playback, OperationSources.Describe(item), operation => RunPlayback(item, userId, context, operation)) == null)
                        _logger.LogWarning("Playback update for {ItemName} was not started because AniDoki storage is unavailable", item.Name);
                }
            } catch (Exception exception) {
                _logger.LogError("Fatal error occured during anime sync job: {Exception}", exception);
            }
        }

        private async Task RunPlayback(BaseItem item, Guid userId, PlaybackNotificationContext context, OperationContext operation) {
            var collector = new PlaybackOutcomeCollector(operation.Record);
            var updateProviderStatus = new UpdateProviderStatus(_libraryManager, _loggerFactory, _httpContextAccessor, _serverApplicationHost, _httpClientFactory, _applicationPaths, _memoryCache, _delayer);
            try {
                await updateProviderStatus.UpdateInsideGate(item, userId, true, collector, SyncUpdateSource.Playback);
            } finally {
                if (collector.HasConfirmedChange)
                    _manualMarks?.Supersede(userId, item.Id);
                _discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, SyncNotificationTrigger.Playback, collector));
                PlaybackNotificationPublisher.Publish(collector, context, userId, _notifications, _logger);
            }
        }
    }
}
