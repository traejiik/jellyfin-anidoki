#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki;

/// <summary>
/// Batches watched marks from clients without the confirmation prompt and runs one operation per user and season,
/// for the highest marked episode.
/// </summary>
public class TaskProcessMarkedMedia {
    private List<(Guid userId, Guid? seasonId, Video baseItem)> itemsToUpdate { get; set; } = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TaskProcessMarkedMedia> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArmClient _arm;
    private readonly AnimeListSource _animeList;
    private readonly IAsyncDelayer _delayer;
    private readonly OperationRunner _runner;
    private readonly DiscordNotificationDispatcher _discord;
    private readonly Matching.ISeasonWatchState _watchState;

    public TaskProcessMarkedMedia(ILoggerFactory loggerFactory, ILibraryManager libraryManager, IMemoryCache memoryCache, IHttpContextAccessor httpContextAccessor, IServerApplicationHost serverApplicationHost, IHttpClientFactory httpClientFactory, ArmClient arm, AnimeListSource animeList, IAsyncDelayer delayer, OperationRunner runner, DiscordNotificationDispatcher discord = null, Matching.ISeasonWatchState watchState = null) {
        _watchState = watchState;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
        _memoryCache = memoryCache;
        _httpContextAccessor = httpContextAccessor;
        _serverApplicationHost = serverApplicationHost;
        _httpClientFactory = httpClientFactory;
        _arm = arm;
        _animeList = animeList;
        _logger = loggerFactory.CreateLogger<TaskProcessMarkedMedia>();
        _delayer = delayer;
        _runner = runner;
        _discord = discord;
    }

    public void AddToUpdateList((Guid userId, Guid? seasonId, Video baseItem) itemToAdd) {
        lock (itemsToUpdate) {
            itemsToUpdate.Add(itemToAdd);
        }
    }

    public async Task RunUpdate() {
        // wait a second to let the list populate itself if a mass update is occuring
        await Task.Delay(1000);

        while (true) {
            List<(Guid userId, Guid? seasonId, Video baseItem)> batch;
            lock (itemsToUpdate) {
                if (itemsToUpdate.Count == 0)
                    return;
                var first = itemsToUpdate[0];
                batch = itemsToUpdate.Where(item => item.seasonId == first.seasonId && item.userId == first.userId).ToList();
                // Removed before running, so a batch that fails never holds up the ones behind it.
                itemsToUpdate.RemoveAll(item => batch.Contains(item));
            }

            try {
                await Run(batch);
            } catch (Exception error) {
                _logger.LogError(error, "Could not update trackers for a watched mark");
            }
        }
    }

    private async Task Run(List<(Guid userId, Guid? seasonId, Video baseItem)> batch) {
        var (userId, _, firstItem) = batch[0];
        var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(uc => uc.UserId == userId);
        if (userConfig == null || !LibraryFilter.Includes(userConfig, _libraryManager, _animeList.Index, _logger, firstItem))
            return;
        var target = batch.Where(item => !SyncHelper.MediaShouldBeIgnored(item.baseItem)).MaxBy(item => item.baseItem.IndexNumber);
        if (target.Equals(default((Guid, Guid?, Video)))) {
            _logger.LogDebug("Ignoring {ItemName}; every paired episode carries the ignore tag", firstItem.Name);
            return;
        }

        if (_memoryCache.TryGetValue("lastQuery", out DateTime lastQuery) && (DateTime.UtcNow - lastQuery).TotalSeconds <= 5) {
            _logger.LogDebug("Waiting 5 seconds between watched-mark updates");
            await Task.Delay(5000);
        }
        _memoryCache.Set("lastQuery", DateTime.UtcNow, new MemoryCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)
        });

        Video item = target.baseItem;
        var handle = _runner.Enqueue(userId, OperationKind.WatchedToggle, OperationSources.Describe(item), operation => Update(item, userId, operation));
        if (handle == null) {
            _logger.LogWarning("Watched-mark update for {ItemName} was not started because AniDoki storage is unavailable", item.Name);
            return;
        }
        // One batch at a time, as before, so the wait above still spaces the requests.
        await handle.Completion;
    }

    private async Task Update(Video item, Guid userId, OperationContext operation) {
        var collector = new PlaybackOutcomeCollector(operation.Record);
        var pipeline = new SyncPipeline(_libraryManager, _loggerFactory, _httpContextAccessor, _serverApplicationHost, _httpClientFactory, _arm, _animeList, _memoryCache, _delayer) { WatchState = _watchState };
        try {
            await pipeline.Run(item, userId, true, collector, SyncUpdateSource.LegacyManual, operation.CancellationToken);
        } finally {
            _discord?.Enqueue(SyncOperationSummary.Capture(item, userId, operation.OperationId, SyncNotificationTrigger.LegacyManual, collector));
        }
    }
}
