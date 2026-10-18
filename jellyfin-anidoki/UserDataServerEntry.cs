using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.ManualMarks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki {
    public class UserDataServerEntry : IHostedService {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IAsyncDelayer _delayer;
        private readonly TaskProcessMarkedMedia _taskProcessMarkedMedia;
        private Task? _updateTask;
        private readonly object _updateLock = new();
        private readonly ManualMarkStore _manualMarks;
        private readonly ManualMarkCapabilityRegistry _capabilities;
        private bool _started;

        public UserDataServerEntry(IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache,
            IApplicationPaths applicationPaths, ManualMarkStore manualMarks, ManualMarkCapabilityRegistry capabilities, Notifications.Discord.DiscordNotificationDispatcher discord, Operations.OperationRunner runner) {
            _manualMarks = manualMarks;
            _capabilities = capabilities;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            loggerFactory.CreateLogger<UpdateProviderStatus>();
            _httpContextAccessor = httpContextAccessor;
            _serverApplicationHost = serverApplicationHost;
            _httpClientFactory = httpClientFactory;
            _memoryCache = memoryCache;
            _applicationPaths = applicationPaths;
            _delayer = new Delayer();
            _taskProcessMarkedMedia = new TaskProcessMarkedMedia(loggerFactory, _libraryManager, _memoryCache, _httpContextAccessor, _serverApplicationHost, _httpClientFactory, _applicationPaths, _delayer, runner, discord);
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            if (!_started)
                _userDataManager.UserDataSaved += UserDataManagerOnUserDataSaved;
            _started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            if (_started)
                _userDataManager.UserDataSaved -= UserDataManagerOnUserDataSaved;
            _started = false;
            return Task.CompletedTask;
        }

        private void UserDataManagerOnUserDataSaved(object? sender, UserDataSaveEventArgs e) {
            if (e.SaveReason == UserDataSaveReason.TogglePlayed) {
                if (!e.UserData.Played)
                    _manualMarks.CancelForSource(e.UserId, e.Item.Id);
                var capture = _httpContextAccessor.HttpContext?.Items[ManualMarkRequestContext.ItemKey] as ManualMarkRequestContext;
                if (capture?.Observe(e.UserId, e.Item.Id, e.UserData.Played) == true || _capabilities.PersistenceUnreadable ||
                    capture == null && _capabilities.HasEnrollmentForUser(e.UserId))
                    return;
            }
            if (e.SaveReason == UserDataSaveReason.TogglePlayed && Plugin.Instance?.PluginConfiguration.watchedTickboxUpdatesProvider == true) {
                if (!e.UserData.Played || e.Item is not Video)
                    return;
                // asynchronous call so it doesn't prevent the UI marking the media as watched
                Episode? episode = e.Item as Episode;
                lock (_updateLock) {
                    _taskProcessMarkedMedia.AddToUpdateList((e.UserId, episode?.Season?.Id, e.Item as Video));
                    if (_updateTask == null || _updateTask.IsCompleted) {
                        _updateTask = _taskProcessMarkedMedia.RunUpdate();
                    }
                }
            }
        }
    }
}
