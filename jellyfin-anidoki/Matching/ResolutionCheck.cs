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
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

/// <summary>
/// Compares automatic resolution with saved matches and the two progress rules for everything a user has watched, and
/// writes a report. It only reads from trackers, through the read methods of <see cref="ITrackerProvider"/>, and never
/// plans or applies anything. "Played" here is the episode most recently played, because the rules only differ when a
/// later episode was already watched; the highest-numbered episode would make them agree by definition.
/// </summary>
public sealed class ResolutionCheck(
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths,
    IMemoryCache memoryCache,
    IAsyncDelayer delayer,
    MatchStore matches,
    ResolutionCheckStore rows,
    ISeasonWatchState watchState,
    Guid userId) {
    private readonly ILogger<ResolutionCheck> _logger = loggerFactory.CreateLogger<ResolutionCheck>();

    /// <summary>Tests set this to substitute an in-memory tracker; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; set; }

    public Task Run(OperationContext operation) => CheckTargets(FindTargets(), operation);

    /// <summary>The most recently played episode of each season, and each watched movie.</summary>
    private List<Video> FindTargets() {
        var targets = new List<Video>();
        var user = users.GetUserById(userId);
        UserConfig? config = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
        if (user == null || config == null)
            return targets;
        foreach (BaseItem item in SyncHelper.GetUsersJellyfinLibrary(userId, users, library)) {
            if (!LibraryFilter.Includes(config, library, _logger, item))
                continue;
            if (item is Movie movie) {
                if (!SyncHelper.MediaShouldBeIgnored(movie) && userData.GetUserData(user, movie) is { Played: true })
                    targets.Add(movie);
            } else if (item is Series series) {
                foreach (Season season in series.Children.OfType<Season>()) {
                    var played = season.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>()
                        .Where(episode => !SyncHelper.MediaShouldBeIgnored(episode))
                        .Select(episode => (Episode: (Video)episode, Data: userData.GetUserData(user, episode)))
                        .Where(entry => entry.Data is { Played: true })
                        .Select(entry => (entry.Episode, entry.Data!.LastPlayedDate))
                        .ToList();
                    if (MostRecentlyPlayed(played) is { } latest)
                        targets.Add(latest);
                }
            }
        }
        return targets;
    }

    /// <summary>Bulk marks can share a date, or have none; the highest episode then stands in.</summary>
    internal static Video? MostRecentlyPlayed(IReadOnlyList<(Video Item, DateTime? LastPlayed)> played) =>
        played.OrderByDescending(entry => entry.LastPlayed ?? DateTime.MinValue).ThenByDescending(entry => entry.Item.IndexNumber).FirstOrDefault().Item;

    internal async Task CheckTargets(IReadOnlyList<Video> targets, OperationContext operation) {
        UserConfig? config = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
        if (config?.UserApiAuth == null) {
            operation.Result = OperationState.NoChange;
            operation.Message = "This user has no linked trackers.";
            return;
        }

        var clients = new TrackerClientFactory(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, _logger) { Override = ClientOverride };
        var sourceIds = new SourceIdResolver(httpClientFactory, applicationPaths, loggerFactory, _logger);
        var automatic = new AutomaticResolver(_logger);
        // A provider that refuses us or rate-limits us will not answer the next season either.
        var stopped = new HashSet<ApiName>();
        int sequence = 0;
        int differing = 0;
        operation.ReportProgress(0, targets.Count);
        for (int i = 0; i < targets.Count; i++) {
            if (operation.CancellationToken.IsCancellationRequested) {
                operation.Message = $"Cancelled after {i} of {targets.Count}.";
                operation.CancellationToken.ThrowIfCancellationRequested();
            }
            if (SyncSource.From(targets[i]) is { } found) {
                var source = found with { IdsOnly = LibraryFilter.Scope(config, library, _logger, targets[i]) == LibraryScope.IdsOnly };
                SourceIds ids = await sourceIds.Resolve(source);
                IReadOnlyList<int> watched = watchState.Watched(source, userId).Select(episode => episode.Number).ToList();
                foreach (UserApiAuth link in config.UserApiAuth) {
                    if (stopped.Contains(link.Name) || clients.Create(link.Name, config) is not { } client)
                        continue;
                    ITrackerProvider tracker = clients.Adapt(link.Name, client);
                    ResolutionCheckRow row;
                    try {
                        row = await Check(source, ids, tracker, automatic, watched, operation.CancellationToken);
                    } catch (TrackerFailureException failure) {
                        row = new ResolutionCheckRow(source.SourceKey, link.Name, CheckResolution.Failed(failure.Kind), null, null, null, false);
                        if (failure.Kind is TrackerFailureKind.Unauthorized or TrackerFailureKind.RateLimited)
                            stopped.Add(link.Name);
                    } catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) {
                        operation.Message = $"Cancelled after {i} of {targets.Count}.";
                        throw;
                    }
                    rows.Add(operation.OperationId, sequence++, row);
                    if (row.Differs)
                        differing++;
                }
                if (i < targets.Count - 1)
                    await delayer.Delay(TimeSpan.FromSeconds(2));
            }
            operation.ReportProgress(i + 1, targets.Count);
        }
        operation.Result = OperationState.Succeeded;
        operation.Message = $"Checked {targets.Count} seasons and movies. Saved answers differing from automatic: {differing}.";
    }

    private async Task<ResolutionCheckRow> Check(SyncSource source, SourceIds ids, ITrackerProvider tracker, AutomaticResolver automatic,
        IReadOnlyList<int> watched, CancellationToken cancellationToken) {
        SourceResolution automaticAnswer = await automatic.Resolve(source, ids, tracker, cancellationToken);
        CheckResolution automaticRow = CheckResolution.From(automaticAnswer);
        Match? match = matches.GetEffective(userId, tracker.Name, source.SourceKey, true);
        CheckResolution? savedRow = null;
        SourceResolution answer = automaticAnswer;
        // No saved match: render the automatic answer as one, which is how highest-watched would treat this season.
        Match? progressMatch = match;
        if (match == null) {
            int last = watched.Append(source.EpisodeNumber).Max();
            var derived = await new RelationChainSegmenter(tracker, _logger).ToMatch(new MatchScope(tracker.Name, source.SourceKey), source, automaticAnswer, last, cancellationToken);
            progressMatch = derived.Match;
        }

        Dictionary<EntryRef, EntryState> entries = progressMatch == null ? [] : await MatchResolver.ReadTargets(progressMatch, tracker, cancellationToken);
        if (match != null) {
            answer = RangeResolver.Resolve(match, source.EpisodeNumber, source.SearchTitle, entries);
            savedRow = CheckResolution.From(answer);
        }

        ResolvedTarget? played = (answer as Resolved)?.Played;
        int? highest = null;
        if (played != null && progressMatch != null) {
            highest = ProgressAggregator.ForPlayback(progressMatch, source.EpisodeNumber, watched, entries)
                .FirstOrDefault(entry => entry.Target.Entry.Id == played.Entry.Id && entry.Target.Entry.AlternativeId == played.Entry.AlternativeId)?.Progress;
            // The real rule leaves an entry that is not behind on the played episode, so the rules agree there.
            if (highest != null && !MatchResolver.IsBehind(entries.GetValueOrDefault(played.Entry)))
                highest = played.TargetEpisode;
        }
        return new ResolutionCheckRow(source.SourceKey, tracker.Name, automaticRow, savedRow, played?.TargetEpisode, highest,
            savedRow != null && !savedRow.SameAs(automaticRow));
    }
}
