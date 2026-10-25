using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki {
    public enum SyncUpdateSource { Playback, LegacyManual, ConfirmedManual }
    public class UpdateProviderStatus {
        private readonly ILogger<UpdateProviderStatus> _logger;
        private readonly TrackerClientFactory _clients;
        private readonly SourceIdResolver _sourceIds;
        private readonly ISourceResolver _resolver;
        private readonly TimeProvider _clock = TimeProvider.System;
        private readonly ILibraryManager _libraryManager;

        private AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse _apiIds = new();

        internal AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ResolvedIds => _apiIds;

        /// <summary>
        /// Builds the provider client for a provider and user. Tests set this to substitute an in-memory tracker;
        /// production leaves it null so the real client is constructed.
        /// </summary>
        internal Func<ApiName, UserConfig, IApiCallHelpers>? ApiCallHelpersFactory {
            get => _clients.Override;
            set => _clients.Override = value;
        }

        public UpdateProviderStatus(ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory,
            IApplicationPaths applicationPaths,
            IMemoryCache memoryCache,
            IAsyncDelayer delayer) {
            _libraryManager = libraryManager;
            _logger = loggerFactory.CreateLogger<UpdateProviderStatus>();
            _clients = new TrackerClientFactory(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, _logger);
            _sourceIds = new SourceIdResolver(httpClientFactory, applicationPaths, loggerFactory, _logger);
            _resolver = new AutomaticResolver(_logger);
        }


        public async Task Update(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector = null,
            SyncUpdateSource source = SyncUpdateSource.Playback, CancellationToken cancellationToken = default) {
            using var lease = await SyncExecutionGate.Shared.Enter(userId, cancellationToken);
            await UpdateInsideGate(e, userId, playedToCompletion, collector, source, cancellationToken);
        }

        internal Task UpdateInsideGate(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector,
            SyncUpdateSource source, CancellationToken cancellationToken = default) =>
            UpdateCore(e, userId, playedToCompletion, collector,
                source == SyncUpdateSource.ConfirmedManual ? PlanPolicy.ConfirmedManual : PlanPolicy.Automatic, cancellationToken);

        private async Task UpdateCore(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector,
            PlanPolicy policy, CancellationToken cancellationToken) {
            if (Plugin.Instance?.StorageError is { } storageError) {
                _logger.LogWarning("Skipping tracker sync because AniDoki storage is unavailable: {StorageError}", storageError);
                return;
            }

            // SyncProviderFromLocal reuses one instance across series, so anything resolved
            // for the previous item must not leak into this one. Without this, an item that
            // resolves no IDs would be written to the previous item's tracker entry.
            _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse();

            var video = e as Video;
            UserConfig? userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
            if (userConfig == null) {
                _logger.LogWarning("The user {UserId} does not exist in the plugins config file. Skipping", userId);
                return;
            }

            if (userConfig.UserApiAuth == null) {
                _logger.LogWarning("The user {UserId} is not authenticated. Skipping", userId);
                return;
            }

            if (LibraryCheck(userConfig, _libraryManager, _logger, e) && video is Episode or Movie && playedToCompletion) {
                var source = SyncSource.From(video);
                if (source == null) {
                    _logger.LogError("Video does not contain required index numbers to sync; skipping");
                    return;
                }

                SourceIds sourceIds = await _sourceIds.Resolve(source);
                _apiIds = sourceIds.Ids;
                var rules = new PlanRules(userConfig.PlanToWatchOnly, userConfig.RewatchCompleted);
                foreach (UserApiAuth userApiAuth in userConfig.UserApiAuth) {
                    _logger.LogDebug("Using provider {ApiName}...", userApiAuth.Name);
                    // continue, not return: an unconfigured provider must not skip the providers after it
                    IApiCallHelpers? client = _clients.Create(userApiAuth.Name, userConfig);
                    if (client == null)
                        continue;
                    ITrackerProvider tracker = _clients.Adapt(userApiAuth.Name, client);
                    var resolution = await _resolver.Resolve(source, sourceIds, tracker, cancellationToken);
                    await new Executor(tracker, rules, policy, collector, _clock, _logger).Execute(resolution, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Checks if the supplied item is in a folder the user wants to track for anime updates.
        /// </summary>
        /// <param name="userConfig">User config.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="item">Item to check location of.</param>
        /// <returns></returns>
        public static bool LibraryCheck(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) {
            try {
                // user has no library filters
                if (userConfig.LibraryToCheck is { Length: 0 }) {
                    return true;
                }

                // item is in a path of a folder the user wants to be monitored
                var topParent = item.GetTopParent();
                if (topParent is not null) {
                    var allLocations = libraryManager.GetVirtualFolders()
                        .Where(item => userConfig.LibraryToCheck.Contains(item.ItemId))
                        .SelectMany(f => f.Locations)
                        .ToHashSet();
                    if (allLocations.Contains(topParent.Path)) {
                        return true;
                    }
                }

                logger.LogInformation("Item is in a folder the user does not want to be monitored; ignoring");
                return false;
            } catch (Exception e) {
                logger.LogInformation("Library check ran into an issue: {ErrorMessage}", e.Message);
                return false;
            }
        }
    }
}
