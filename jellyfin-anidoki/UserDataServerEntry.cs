using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki.Settings;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

/// <summary>
/// Listens to Jellyfin's user-data saves. A watched toggle either waits for the user's confirmation (manual marks)
/// or, with the watched-tickbox setting on, is handed to <see cref="WatchedToggleTrigger"/>.
/// </summary>
public class UserDataServerEntry : IHostedService {
    private readonly IUserDataManager _userDataManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ManualMarkStore _manualMarks;
    private readonly ManualMarkCapabilityRegistry _capabilities;
    private readonly WatchedToggleTrigger _toggles;
    private readonly CompletionLedger _ledger;
    private readonly ILogger<UserDataServerEntry> _logger;
    private bool _started;

    public UserDataServerEntry(IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        IHttpContextAccessor httpContextAccessor,
        IServerApplicationHost serverApplicationHost,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        ArmClient arm, AnimeListSource animeList, ManualMarkStore manualMarks, ManualMarkCapabilityRegistry capabilities,
        DiscordNotificationDispatcher? discord, OperationRunner runner, CompletionLedger ledger, ISeasonWatchState? watchState) {
        _userDataManager = userDataManager;
        _httpContextAccessor = httpContextAccessor;
        _manualMarks = manualMarks;
        _capabilities = capabilities;
        _ledger = ledger;
        _logger = loggerFactory.CreateLogger<UserDataServerEntry>();
        _toggles = new WatchedToggleTrigger(loggerFactory, libraryManager, memoryCache, httpContextAccessor, serverApplicationHost,
            httpClientFactory, arm, animeList, new Delayer(), runner, ledger, discord, watchState);
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
        if (e.SaveReason != UserDataSaveReason.TogglePlayed)
            return;
        if (!e.UserData.Played) {
            _manualMarks.CancelForSource(e.UserId, e.Item.Id);
            EndWindow(e);
        }
        var capture = _httpContextAccessor.HttpContext?.Items[ManualMarkRequestContext.ItemKey] as ManualMarkRequestContext;
        if (capture?.Observe(e.UserId, e.Item.Id, e.UserData.Played) == true || _capabilities.PersistenceUnreadable ||
            capture == null && _capabilities.HasEnrollmentForUser(e.UserId))
            return;
        if (Plugin.Instance?.PluginConfiguration.watchedTickboxUpdatesProvider != true)
            return;
        if (!e.UserData.Played || e.Item is not Video video)
            return;
        // Queued, so the client's save is not held up by the tracker update.
        Episode? episode = video as Episode;
        _toggles.Add(new ToggleMark(e.UserId, episode?.Season?.Id, video));
    }

    // Unmarking writes nothing to trackers, but the next watch of the item counts again.
    private void EndWindow(UserDataSaveEventArgs e) {
        try {
            if (_ledger.EndWindow(e.UserId, e.Item.Id) > 0)
                _logger.LogDebug("Cleared the recorded watch of {ItemName}, because it was marked unwatched", e.Item.Name);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            _logger.LogDebug("Could not clear the recorded watch of {ItemName}: {ErrorMessage}", e.Item.Name, error.Message);
        }
    }
}
