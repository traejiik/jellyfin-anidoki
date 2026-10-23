using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.ManualMarks;
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

        internal IApiCallHelpers ApiCallHelpers;
        private UserConfig? _userConfig;
        private PlaybackOutcomeCollector? _outcomeCollector;
        private SyncUpdateSource _source;
        private Type? _animeType;
        private readonly ILibraryManager _libraryManager;

        internal ApiName ApiName;
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


        public async Task Update(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector = null, SyncUpdateSource source = SyncUpdateSource.Playback) {
            using var lease = await SyncExecutionGate.Shared.Enter(userId);
            await UpdateInsideGate(e, userId, playedToCompletion, collector, source);
        }

        internal async Task UpdateInsideGate(BaseItem e, Guid userId, bool playedToCompletion, PlaybackOutcomeCollector? collector, SyncUpdateSource source) {
            _outcomeCollector = collector;
            _source = source;
            try { await UpdateCore(e, userId, playedToCompletion); } finally { _outcomeCollector = null; _source = SyncUpdateSource.Playback; }
        }

        private async Task UpdateCore(BaseItem e, Guid userId, bool playedToCompletion) {
            if (Plugin.Instance?.StorageError is { } storageError) {
                _logger.LogWarning("Skipping tracker sync because AniDoki storage is unavailable: {StorageError}", storageError);
                return;
            }

            // SyncProviderFromLocal reuses one instance across series, so anything resolved
            // for the previous item must not leak into this one. Without this, an item that
            // resolves no IDs would be written to the previous item's tracker entry.
            _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse();
            _animeType = null;

            var video = e as Video;
            if (video is Episode) {
                _animeType = typeof(Episode);
            } else if (video is Movie) {
                _animeType = typeof(Movie);
            }

            _userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
            if (_userConfig == null) {
                _logger.LogWarning("The user {UserId} does not exist in the plugins config file. Skipping", userId);
                return;
            }

            if (_userConfig.UserApiAuth == null) {
                _logger.LogWarning("The user {UserId} is not authenticated. Skipping", userId);
                return;
            }

            if (LibraryCheck(_userConfig, _libraryManager, _logger, e) && video is Episode or Movie && playedToCompletion) {
                var source = SyncSource.From(video);
                if (source == null) {
                    _logger.LogError("Video does not contain required index numbers to sync; skipping");
                    return;
                }

                SourceIds sourceIds = await _sourceIds.Resolve(source);
                _apiIds = sourceIds.Ids;
                foreach (UserApiAuth userApiAuth in _userConfig.UserApiAuth) {
                    ApiName = userApiAuth.Name;
                    _logger.LogDebug("Using provider {ApiName}...", userApiAuth.Name);
                    // continue, not return: an unconfigured provider must not skip the providers after it
                    IApiCallHelpers? client = _clients.Create(userApiAuth.Name, _userConfig);
                    if (client == null)
                        continue;
                    ApiCallHelpers = client;
                    var resolution = await _resolver.Resolve(source, sourceIds, _clients.Adapt(userApiAuth.Name, client), CancellationToken.None);
                    await SyncResolution(resolution);
                }
            }
        }

        /// <summary>
        /// Gets anime's title.
        /// </summary>
        /// <param name="anime">The API anime</param>
        /// <returns>
        /// <see cref="Anime.Title"/> if it isn't empty.<br/>
        /// If it is, then the first <see cref="AlternativeTitles.Synonyms">synonym</see>.<br/>
        /// If there isn't any, then the <see cref="AlternativeTitles.Ja">japanese title</see>.
        /// </returns>
        private static string GetAnimeTitle(Anime anime) {
            var title = string.IsNullOrWhiteSpace(anime.Title)
                ? anime.AlternativeTitles.Synonyms.Count > 0
                    ? anime.AlternativeTitles.Synonyms[0]
                    : anime.AlternativeTitles.Ja
                : anime.Title;
            return title;
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


        private async Task CheckUserListAnimeStatus(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse matchingIds, int episodeNumber, string title, bool overrideCheckRewatch, string? alternativeId = null) {
            Anime detectedAnime = await GetAnime(matchingIds, title, alternativeId: alternativeId);

            if (detectedAnime == null) {
                _outcomeCollector?.Record(new ProviderWriteOutcome(ApiName, alternativeId ?? "", title ?? "", OutcomeKind.Unresolved,
                    null, null, ApiName == ApiName.Annict, "resolve", false));
            }
            await CheckUserListAnimeStatusBase(detectedAnime, episodeNumber, overrideCheckRewatch, alternativeId);
        }

        private async Task CheckUserListAnimeStatus(int matchingAnimeId, int episodeNumber, bool overrideCheckRewatch, string? alternativeId = null) {
            Anime detectedAnime = await GetAnime(matchingAnimeId, alternativeId: alternativeId);

            if (detectedAnime == null) {
                _outcomeCollector?.Record(new ProviderWriteOutcome(ApiName, alternativeId ?? matchingAnimeId.ToString(), "", OutcomeKind.Unresolved,
                    null, null, ApiName == ApiName.Annict, "resolve", false));
            }
            await CheckUserListAnimeStatusBase(detectedAnime, episodeNumber, overrideCheckRewatch, alternativeId);
        }

        /// <summary>
        /// Turns a resolution into today's writes: earlier parts complete in chain order, then the played entry is read
        /// again and decided on. PR 6b replaces this with the planner and executor.
        /// </summary>
        private async Task SyncResolution(SourceResolution resolution) {
            foreach (var part in resolution.EarlierParts)
                await CheckUserListAnimeStatus(part.Entry.Id, part.TargetEpisode, overrideCheckRewatch: false, alternativeId: part.Entry.AlternativeId);
            switch (resolution) {
                case Resolved { Played.Entry.Lookup: { } lookup } resolved:
                    await CheckUserListAnimeStatus(_apiIds, resolved.Played.TargetEpisode, lookup.Title, overrideCheckRewatch: false);
                    break;
                case Resolved resolved:
                    await CheckUserListAnimeStatus(resolved.Played.Entry.Id, resolved.Played.TargetEpisode, overrideCheckRewatch: false, alternativeId: resolved.Played.Entry.AlternativeId);
                    break;
                case Unresolved { Record: { } record }:
                    _outcomeCollector?.Record(new ProviderWriteOutcome(ApiName, record.TargetId, record.Title, OutcomeKind.Unresolved,
                        null, null, ApiName == ApiName.Annict, "resolve", false));
                    break;
            }
        }

        private async Task CheckUserListAnimeStatusBase(Anime? detectedAnime, int episodeNumber, bool overrideCheckRewatch, string? alternativeId = null) {
            if (detectedAnime == null) {
                _logger.LogInformation("({ApiName}) Could not retrieve the matched anime from the provider; not updating", ApiName);
                return;
            }
            if (_source == SyncUpdateSource.ConfirmedManual && !ManualMarkProgressRules.Allows(detectedAnime, episodeNumber, ApiName == ApiName.Annict)) {
                RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.NoChange, "manual-forward-only");
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) is already at or past episode {EpisodeNumber}, completed or rewatching; manual marks only move progress forward. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), episodeNumber);
                return;
            }
            if (detectedAnime.MyListStatus != null &&
                (detectedAnime.MyListStatus.Status == Status.Watching || detectedAnime.MyListStatus.Status == Status.Rewatching) &&
                ApiName != ApiName.Annict) {
                _logger.LogDebug("({ApiName}) {MediaKind} ({Title}) found on {ListStatus} list", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), detectedAnime.MyListStatus.Status);
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
                return;
            }

            // only plan to watch
            if (_userConfig.PlanToWatchOnly) {
                bool updated = false;
                if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Plan_to_watch) {
                    _logger.LogDebug("({ApiName}) {MediaKind} ({Title}) found on plan to watch list", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                    if (ApiName == ApiName.Annict) {
                        updated = true;
                        await UpdateAnnictStatus(detectedAnime, episodeNumber);
                    } else {
                        updated = true;
                        await UpdateAnimeStatus(detectedAnime, episodeNumber);
                    }
                } else if (ApiName != ApiName.Annict) {
                    // also check if rewatch completed is checked
                    _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) not found in plan to watch list{CompletedListNote}", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), (_userConfig.RewatchCompleted ? ", checking completed list.." : null));
                    updated = true;
                    await CheckIfRewatchCompleted(detectedAnime, episodeNumber, overrideCheckRewatch);
                }

                if (!updated) {
                    RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.Skipped, "plan-to-watch-only");
                    _logger.LogInformation("({ApiName}) Could not update.", ApiName);
                }

                return;
            }

            _logger.LogInformation("User does not have plan to watch only ticked");

            if (ApiName != ApiName.Annict) {
                // check if rewatch completed is checked
                if (await CheckIfRewatchCompleted(detectedAnime, episodeNumber, overrideCheckRewatch)) {
                    return;
                }
            }

            // everything else
            if (detectedAnime.MyListStatus != null) {
                // anime is on user list
                _logger.LogDebug("({ApiName}) {MediaKind} ({Title}) found on {ListStatus} list", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), detectedAnime.MyListStatus.Status);
                if (detectedAnime.MyListStatus.Status == Status.Completed) {
                    RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.Skipped, "rewatch-disabled");
                    if (ApiName != ApiName.Annict)
                        _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found on Completed list, but user does not want to automatically set as rewatching. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                    return;
                }
            } else {
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) not on user list", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
            }

            if (ApiName == ApiName.Annict) {
                await UpdateAnnictStatus(detectedAnime, episodeNumber);
            } else {
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
            }
        }

        internal async Task UpdateAnnictStatus(Anime detectedAnime, int episodeNumber, PlaybackOutcomeCollector? collector = null) {
            collector ??= _outcomeCollector;
            // rewatching isnt supported by annict; skip
            if (detectedAnime.MyListStatus?.Status == Status.Completed) {
                RecordDecision(collector, detectedAnime, OutcomeKind.Skipped, "rewatch-unsupported");
                return;
            }
            if (_userConfig?.PlanToWatchOnly == true && detectedAnime.MyListStatus is null) {
                RecordDecision(collector, detectedAnime, OutcomeKind.Skipped, "plan-to-watch-only");
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found, but not on plan to watch list", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                return;
            }

            if (detectedAnime.NumEpisodes == episodeNumber) {
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) complete, marking anime as complete", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                await ObserveAnimeWrite(collector, detectedAnime, 1, Status.Completed, alternativeId: detectedAnime.AlternativeId, ids: _apiIds);
                return;
            }

            if (detectedAnime.NumEpisodes > episodeNumber && detectedAnime.MyListStatus?.Status != Status.Watching) {
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) being marked as watching", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                await ObserveAnimeWrite(collector, detectedAnime, 1, Status.Watching, alternativeId: detectedAnime.AlternativeId, ids: _apiIds);
            } else {
                RecordDecision(collector, detectedAnime, OutcomeKind.NoChange, "status");
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) already set to watching, not updating again", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
            }
        }

        private async Task<bool> CheckIfRewatchCompleted(Anime detectedAnime, int indexNumber, bool overrideCheckRewatch) {
            if (overrideCheckRewatch ||
                detectedAnime.MyListStatus is { Status: Status.Completed } ||
                detectedAnime.MyListStatus is { Status: Status.Rewatching } && detectedAnime.MyListStatus.NumEpisodesWatched < indexNumber) {
                if (ApiName == ApiName.Simkl) {
                    RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.Skipped, "rewatch-unsupported");
                    _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found on completed list, but {ApiName} does not support re-watching. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), ApiName);
                    return true;
                }
                if (_userConfig.RewatchCompleted) {
                    if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Completed) {
                        _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found on completed list, setting as re-watching", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                        await UpdateAnimeStatus(detectedAnime, indexNumber, true, detectedAnime.MyListStatus.RewatchCount, true);
                        return true;
                    }
                } else {
                    RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.Skipped, "rewatch-disabled");
                    _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found on Completed list, but user does not want to automatically set as rewatching. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                    return true;
                }
            } else if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.NumEpisodesWatched >= indexNumber) {
                RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.NoChange, "already-watched");
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found, but provider reports episode already watched. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                return true;
            } else if (_userConfig.PlanToWatchOnly) {
                RecordDecision(_outcomeCollector, detectedAnime, OutcomeKind.Skipped, "plan-to-watch-only");
                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) found, but not on completed or plan to watch list. Skipping", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get a single result from a user anime search.
        /// </summary>
        /// <param name="animeId">ID of the anime you want to get.</param>
        /// <param name="status">User status of the show.</param>
        /// <returns>Single anime result.</returns>
        private async Task<Anime> GetAnime(int animeId, Status? status = null, string? alternativeId = null) {
            Anime anime = await ApiCallHelpers.GetAnime(animeId, alternativeId: alternativeId);

            if (anime != null && ((status != null && anime.MyListStatus != null && anime.MyListStatus.Status == status) || status == null)) {
                return anime;
            }

            return null;
        }

        private async Task<Anime> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse animeIds, string title, Status? status = null, string? alternativeId = null) {
            Anime anime = await ApiCallHelpers.GetAnime(animeIds, title);

            if (anime != null && ((status != null && anime.MyListStatus != null && anime.MyListStatus.Status == status) || status == null)) {
                return anime;
            }

            return null;
        }

        /// <summary>
        /// Update a users anime status.
        /// </summary>
        /// <param name="detectedAnime">The anime search result to update.</param>
        /// <param name="episodeNumber">The episode number to update the anime to.</param>
        /// <param name="setRewatching">Whether to set the show as being re-watched or not.</param>
        internal async Task UpdateAnimeStatus(Anime detectedAnime, int? episodeNumber, bool? setRewatching = null, int? rewatchCount = null, bool firstTimeRewatch = false, PlaybackOutcomeCollector? collector = null) {
            collector ??= _outcomeCollector;
            if (episodeNumber != null) {
                UpdateAnimeStatusResponse response;
                if (detectedAnime.MyListStatus != null) {
                    if (detectedAnime.MyListStatus.NumEpisodesWatched < episodeNumber.Value || detectedAnime.NumEpisodes == 1 ||
                        (setRewatching != null && setRewatching.Value && detectedAnime.MyListStatus.NumEpisodesWatched == episodeNumber.Value)) {
                        // covers the very rare occurence of re-watching the show and starting at the last episode
                        // movie or ova has only one episode, so just mark it as finished
                        if (episodeNumber.Value == detectedAnime.NumEpisodes || detectedAnime.NumEpisodes == 1) {
                            // either watched all episodes or the anime only has a single episode (ova)
                            if (detectedAnime.NumEpisodes == 1) {
                                // its a movie or ova since it only has one "episode", so the start and end date is the same
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    1,
                                    Status.Completed,
                                    startDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    isRewatching: false,
                                    numberOfTimesRewatched: (setRewatching != null && setRewatching.Value) || detectedAnime.MyListStatus.IsRewatching ? detectedAnime.MyListStatus.RewatchCount + 1 : null,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                // user has reached the number of episodes in the anime, set as completed
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    isRewatching: false,
                                    numberOfTimesRewatched: (setRewatching != null && setRewatching.Value) || detectedAnime.MyListStatus.IsRewatching ? detectedAnime.MyListStatus.RewatchCount + 1 : null,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            }

                            _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) complete, marking anime as complete{RewatchNote}", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime), (ApiName != ApiName.Mal && (setRewatching != null && setRewatching.Value) ? ", increasing re-watch count by 1" : ""));
                            if ((detectedAnime.MyListStatus.IsRewatching || (detectedAnime.NumEpisodes == 1 && detectedAnime.MyListStatus.Status == Status.Completed) || (setRewatching != null && setRewatching.Value)) && ApiName == ApiName.Mal && response != null) {
                                // also increase number of times re-watched by 1
                                // only way to get the number of times re-watched is by doing the update and capturing the response, and then re-updating for MAL :/
                                _logger.LogInformation("({ApiName}) {MediaKind} ({Title}) has also been re-watched, increasing re-watch count by 1", ApiName, (_animeType == typeof(Episode) ? "Series" : "Movie"), GetAnimeTitle(detectedAnime));
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    numberOfTimesRewatched: response.NumTimesRewatched + 1,
                                    isRewatching: false,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            }
                        } else {
                            if (detectedAnime.MyListStatus.IsRewatching && ApiName == ApiName.Mal) {
                                // MAL likes to mark re-watching shows as completed, instead of watching. I guess technically both are correct
                                _logger.LogInformation("({ApiName}) User is re-watching {MediaKind} ({Title}), set as completed but update re-watch progress", ApiName, (_animeType == typeof(Episode) ? "series" : "movie"), GetAnimeTitle(detectedAnime));
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    isRewatching: true,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                Status watchingStatus = detectedAnime.MyListStatus.IsRewatching &&
                                    (ApiName == ApiName.AniList || ApiName == ApiName.Shikimori)
                                        ? Status.Rewatching
                                        : Status.Watching;
                                if (episodeNumber > 1) {
                                    // don't set start date after first episode
                                    response = await ObserveAnimeWrite(collector, detectedAnime,
                                        episodeNumber.Value,
                                        watchingStatus,
                                        alternativeId: detectedAnime.AlternativeId,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                } else {
                                    _logger.LogInformation("({ApiName}) Setting new {MediaKind} ({Title}) as {WatchingStatus}.", ApiName, (_animeType == typeof(Episode) ? "series" : "movie"), GetAnimeTitle(detectedAnime), watchingStatus);
                                    response = await ObserveAnimeWrite(collector, detectedAnime,
                                        episodeNumber.Value,
                                        watchingStatus,
                                        startDate: DateTime.Now,
                                        alternativeId: detectedAnime.AlternativeId,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                }
                            }
                        }

                        if (response != null) {
                            _logger.LogInformation("({ApiName}) Updated {MediaKind} ({Title}) progress to {EpisodeNumber}", ApiName, (_animeType == typeof(Episode) ? "series" : "movie"), GetAnimeTitle(detectedAnime), episodeNumber.Value);
                        } else {
                            _logger.LogError("({ApiName}) Could not update anime status", ApiName);
                        }
                    } else {
                        if (setRewatching != null && setRewatching.Value) {
                            _logger.LogInformation("({ApiName}) Series ({Title}) has already been watched, marking anime as re-watching; progress of {EpisodeNumber}", ApiName, GetAnimeTitle(detectedAnime), episodeNumber.Value);
                            if (ApiName == ApiName.Kitsu && firstTimeRewatch) {
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    episodeNumber.Value,
                                    Status.Rewatching,
                                    true,
                                    detectedAnime.MyListStatus.RewatchCount + 1,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                response = await ObserveAnimeWrite(collector, detectedAnime,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    true,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                                // anilist seems to (at the moment) not allow you to set the show as rewatching and the progress at the same time; going to have to do a separate call
                                if (ApiName == ApiName.AniList) {
                                    response = await ObserveAnimeWrite(collector, detectedAnime,
                                        episodeNumber.Value,
                                        Status.Completed,
                                        true,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                }
                            }
                        } else {
                            response = null;
                            RecordDecision(collector, detectedAnime, OutcomeKind.NoChange, "already-watched");
                            _logger.LogInformation("({ApiName}) Provider reports episode already watched; not updating", ApiName);
                        }
                    }
                } else {
                    // status is not set, must be a new anime
                    if (episodeNumber.Value == detectedAnime.NumEpisodes) {
                        // anime completed all at once or user has watched last episode
                        _logger.LogInformation("({ApiName}) Adding new {MediaKind} ({Title}) to user list as completed with a progress of {EpisodeNumber}", ApiName, (_animeType == typeof(Episode) ? "series" : "movie"), GetAnimeTitle(detectedAnime), episodeNumber.Value);
                        response = await ObserveAnimeWrite(collector, detectedAnime,
                            episodeNumber.Value,
                            Status.Completed,
                            alternativeId: detectedAnime.AlternativeId,
                            ids: _apiIds,
                            startDate: DateTime.Now,
                            endDate: DateTime.Now,
                            isShow: _animeType == typeof(Episode));
                    } else {
                        // not on last episodes so must still be watching
                        _logger.LogInformation("({ApiName}) Adding new {MediaKind} ({Title}) to user list as watching with a progress of {EpisodeNumber}", ApiName, (_animeType == typeof(Episode) ? "series" : "movie"), GetAnimeTitle(detectedAnime), episodeNumber.Value);
                        response = await ObserveAnimeWrite(collector, detectedAnime,
                            episodeNumber.Value,
                            Status.Watching,
                            alternativeId: detectedAnime.AlternativeId,
                            ids: _apiIds,
                            startDate: episodeNumber == 1 ? DateTime.Now : null,
                            isShow: _animeType == typeof(Episode));
                    }
                }

                if (response == null) {
                    _logger.LogError("({ApiName}) Could not update anime status", ApiName);
                }
            }
        }

        private void RecordDecision(PlaybackOutcomeCollector? collector, Anime target, OutcomeKind kind, string step) {
            collector?.Record(new ProviderWriteOutcome(ApiName, target.AlternativeId ?? target.Id.ToString(), GetAnimeTitle(target) ?? "",
                kind, null, null, ApiName == ApiName.Annict, step, false));
        }

        private Task<UpdateAnimeStatusResponse?> ObserveAnimeWrite(PlaybackOutcomeCollector? collector, Anime target,
            int numberOfWatchedEpisodes, Status status, bool? isRewatching = null, int? numberOfTimesRewatched = null,
            DateTime? startDate = null, DateTime? endDate = null, string? alternativeId = null,
            AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null) {
            if (_source == SyncUpdateSource.ConfirmedManual &&
                (!ManualMarkProgressRules.Allows(target, numberOfWatchedEpisodes, ApiName == ApiName.Annict) ||
                 isRewatching == true || numberOfTimesRewatched.HasValue)) {
                RecordDecision(collector, target, OutcomeKind.NoChange, "manual-forward-only");
                return Task.FromResult<UpdateAnimeStatusResponse?>(null);
            }
            Task<UpdateAnimeStatusResponse?> Write() => ApiCallHelpers.UpdateAnime(target.Id, numberOfWatchedEpisodes,
                status, isRewatching, numberOfTimesRewatched, startDate, endDate, alternativeId, ids, isShow);
            if (collector == null)
                return Write();
            bool meaningfulChange = target.MyListStatus == null || status != target.MyListStatus.Status ||
                (ApiName != ApiName.Annict && (numberOfWatchedEpisodes != target.MyListStatus.NumEpisodesWatched ||
                 (isRewatching.HasValue && isRewatching != target.MyListStatus.IsRewatching) ||
                 (numberOfTimesRewatched.HasValue && numberOfTimesRewatched != target.MyListStatus.RewatchCount)));
            return collector.ObserveWrite(ApiName, target, numberOfWatchedEpisodes, status,
                numberOfTimesRewatched.HasValue ? "rewatch-count" : ApiName == ApiName.Annict ? "status" : "progress",
                Write, meaningfulChange);
        }
    }
}
