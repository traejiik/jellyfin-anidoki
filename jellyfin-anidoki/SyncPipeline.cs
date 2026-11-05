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

namespace jellyfin_anidoki;

public enum SyncUpdateSource { Playback, LegacyManual, ConfirmedManual, ManualSync }

/// <summary>
/// Syncs one watched item to every tracker the user has linked: resolve, plan, re-check, write, record. A provider's
/// failure stops that provider only. The caller must already hold the user's <see cref="SyncExecutionGate"/>.
/// </summary>
public class SyncPipeline {
    private readonly ILogger<SyncPipeline> _logger;
    private readonly TrackerClientFactory _clients;
    private readonly SourceIdResolver _sourceIds;
    private readonly ISourceResolver _resolver;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ILibraryManager _libraryManager;

    private AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse _apiIds = new();

    internal AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ResolvedIds => _apiIds;

    /// <summary>Tests set this to substitute an in-memory tracker; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride {
        get => _clients.Override;
        set => _clients.Override = value;
    }

    public SyncPipeline(ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        IHttpContextAccessor httpContextAccessor,
        IServerApplicationHost serverApplicationHost,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        IMemoryCache memoryCache,
        IAsyncDelayer delayer) {
        _libraryManager = libraryManager;
        _logger = loggerFactory.CreateLogger<SyncPipeline>();
        _clients = new TrackerClientFactory(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, _logger);
        _sourceIds = new SourceIdResolver(httpClientFactory, applicationPaths, loggerFactory, _logger);
        _resolver = new AutomaticResolver(_logger);
    }

    internal async Task Run(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector,
        SyncUpdateSource source, CancellationToken cancellationToken = default) {
        PlanPolicy policy = source switch {
            SyncUpdateSource.ConfirmedManual => PlanPolicy.ConfirmedManual,
            SyncUpdateSource.ManualSync => PlanPolicy.ManualSync,
            _ => PlanPolicy.Automatic
        };
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

        if (LibraryFilter.Includes(userConfig, _libraryManager, _logger, e) && video is Episode or Movie && playedToCompletion) {
            var syncSource = SyncSource.From(video);
            if (syncSource == null) {
                _logger.LogError("Video does not contain required index numbers to sync; skipping");
                return;
            }

            SourceIds sourceIds = await _sourceIds.Resolve(syncSource);
            _apiIds = sourceIds.Ids;
            var rules = new PlanRules(userConfig.PlanToWatchOnly, userConfig.RewatchCompleted);
            foreach (UserApiAuth userApiAuth in userConfig.UserApiAuth) {
                _logger.LogDebug("Using provider {ApiName}...", userApiAuth.Name);
                // continue, not return: an unconfigured provider must not skip the providers after it
                ITrackerClient? client = _clients.Create(userApiAuth.Name, userConfig);
                if (client == null)
                    continue;
                ITrackerProvider tracker = _clients.Adapt(userApiAuth.Name, client);
                var executor = new Executor(tracker, rules, policy, collector, _clock, _logger, new ActionSource(video.Id, syncSource.EpisodeNumber));
                SourceResolution resolution;
                try {
                    resolution = await _resolver.Resolve(syncSource, sourceIds, tracker, cancellationToken);
                } catch (TrackerFailureException failure) {
                    // continue, not rethrow: one provider's failure must not stop the providers after it
                    executor.RecordResolutionFailure(failure, syncSource.SearchTitle ?? "");
                    continue;
                }

                await executor.Execute(resolution, cancellationToken);
            }
        }
    }
}
