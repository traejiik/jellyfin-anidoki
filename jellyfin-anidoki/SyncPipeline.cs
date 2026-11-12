using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
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

/// <summary>One linked provider's answer for one item, before anything is written.</summary>
/// <param name="Resolution">Null when resolving failed; <paramref name="Failure"/> then says why.</param>
internal sealed record ProviderPass(ITrackerProvider Tracker, Executor Executor, SyncSource Source, SourceResolution? Resolution,
    TrackerFailureException? Failure);

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

    /// <summary>What a matched season's progress is worked out from. Without it, only the played episode counts.</summary>
    internal ISeasonWatchState? WatchState { get; set; }

    /// <summary>The one linked tracker to sync, or null for every linked tracker. Only manual sync sets it.</summary>
    internal ApiName? OnlyProvider { get; set; }

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

    internal Task Run(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector,
        SyncUpdateSource source, CancellationToken cancellationToken = default) =>
        ForEachProvider(e, userId, playedToCompletion, collector, source, null, async pass => {
            if (pass.Failure is { } failure) {
                pass.Executor.RecordResolutionFailure(failure, pass.Source.SearchTitle ?? "");
                return;
            }

            await pass.Executor.Execute(pass.Resolution!, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Resolves one item for each linked provider, in link order, and hands each answer to <paramref name="handle"/> before
    /// asking the next provider. <see cref="Run"/> executes the answers; a preview plans them without writing.
    /// </summary>
    /// <param name="skip">Providers not to ask at all this time; nothing is read from them.</param>
    internal async Task ForEachProvider(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector,
        SyncUpdateSource source, Func<ApiName, bool>? skip, Func<ProviderPass, Task> handle, CancellationToken cancellationToken) {
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

        LibraryScope scope = LibraryFilter.Scope(userConfig, _libraryManager, _logger, e);
        if (scope != LibraryScope.Excluded && video is Episode or Movie && playedToCompletion) {
            var syncSource = SyncSource.From(video);
            if (syncSource == null) {
                _logger.LogError("Video does not contain required index numbers to sync; skipping");
                return;
            }
            syncSource = syncSource with { IdsOnly = scope == LibraryScope.IdsOnly };

            SourceIds sourceIds = await _sourceIds.Resolve(syncSource);
            _apiIds = sourceIds.Ids;
            var rules = new PlanRules(userConfig.PlanToWatchOnly, userConfig.RewatchCompleted);
            ISourceResolver resolver = new MatchResolver(_resolver, new MatchStore(Plugin.Instance!.Database, _clock), userId,
                Plugin.Instance.PluginConfiguration.applySavedMatches, WatchState,
                source == SyncUpdateSource.ManualSync ? MatchProgressScope.WholeSeason : MatchProgressScope.PlayedEpisode, _logger);
            foreach (UserApiAuth userApiAuth in userConfig.UserApiAuth) {
                if (OnlyProvider is { } only && userApiAuth.Name != only)
                    continue;
                if (skip?.Invoke(userApiAuth.Name) == true)
                    continue;
                _logger.LogDebug("Using provider {ApiName}...", userApiAuth.Name);
                // continue, not return: an unconfigured provider must not skip the providers after it
                ITrackerClient? client = _clients.Create(userApiAuth.Name, userConfig);
                if (client == null)
                    continue;
                ITrackerProvider tracker = _clients.Adapt(userApiAuth.Name, client);
                var executor = new Executor(tracker, rules, policy, collector, _clock, _logger, new ActionSource(video.Id, syncSource.EpisodeNumber));
                SourceResolution resolution;
                try {
                    resolution = await resolver.Resolve(syncSource, sourceIds, tracker, cancellationToken);
                } catch (TrackerFailureException failure) {
                    // continue, not rethrow: one provider's failure must not stop the providers after it
                    await handle(new ProviderPass(tracker, executor, syncSource, null, failure));
                    continue;
                }

                await handle(new ProviderPass(tracker, executor, syncSource, resolution, null));
            }
        }
    }
}
