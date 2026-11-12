using System;
using System.Collections.Generic;
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
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Preview;

/// <summary>
/// Works out what a Jellyfin → Trackers manual sync would change, and changes nothing. It walks the same seasons as
/// <see cref="SyncProviderFromLocal"/>, resolves them through the same pipeline, plans with <see cref="Executor.Plan"/>
/// (which never calls <see cref="ITrackerProvider.Apply"/>), and stores one row per target. Runs inside an operation,
/// which already holds the user's sync gate.
/// </summary>
public sealed class OutboundPreview(
    IUserManager userManager,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths,
    IMemoryCache memoryCache,
    IAsyncDelayer delayer,
    MatchStore matches,
    PreviewStore rows,
    ISeasonWatchState watchState,
    Guid userId) {
    private readonly ILogger<OutboundPreview> _logger = loggerFactory.CreateLogger<OutboundPreview>();

    /// <summary>Tests set this to substitute an in-memory tracker; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; set; }

    /// <summary>The one linked tracker to preview, or null for every linked tracker.</summary>
    internal ApiName? OnlyProvider { get; set; }

    public Task Run(OperationContext operation) =>
        PreviewEpisodes(LatestWatchedEpisodes.For(userId, userManager, libraryManager, _logger), operation);

    internal async Task PreviewEpisodes(IReadOnlyList<Episode> targets, OperationContext operation) {
        var pipeline = new SyncPipeline(libraryManager, loggerFactory, httpContextAccessor, serverApplicationHost, httpClientFactory,
            applicationPaths, memoryCache, delayer) {
            ClientOverride = ClientOverride,
            WatchState = watchState,
            OnlyProvider = OnlyProvider
        };
        bool applySavedMatches = Plugin.Instance?.PluginConfiguration.applySavedMatches ?? false;
        // A tracker that refuses us or rate-limits us will not answer the next season either.
        var stopped = new HashSet<ApiName>();
        var tally = new Dictionary<PreviewRowKind, int>();
        int sequence = 0;
        bool incomplete = false;
        operation.ReportProgress(0, targets.Count);
        for (int i = 0; i < targets.Count; i++) {
            if (operation.CancellationToken.IsCancellationRequested) {
                operation.Message = Cancelled(i, targets.Count);
                operation.CancellationToken.ThrowIfCancellationRequested();
            }
            Episode episode = targets[i];
            if (SyncHelper.MediaShouldBeIgnored(episode)) {
                _logger.LogDebug("(Preview) Ignoring {ItemName}; ignore tag detected", episode.Name);
            } else {
                try {
                    await pipeline.ForEachProvider(episode, userId, true, null, SyncUpdateSource.ManualSync, stopped.Contains, async pass => {
                        foreach (PreviewRow row in await RowsFor(pass, applySavedMatches, operation.CancellationToken)) {
                            rows.Add(operation.OperationId, row with { Seq = sequence++ });
                            tally[row.Kind] = tally.GetValueOrDefault(row.Kind) + 1;
                            if (row is { Kind: PreviewRowKind.Failed, Reason: "unauthorized" or "rate-limited" })
                                stopped.Add(row.Provider);
                        }
                    }, operation.CancellationToken);
                } catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) {
                    operation.Message = Cancelled(i, targets.Count);
                    throw;
                } catch (Exception e) {
                    incomplete = true;
                    _logger.LogError("(Preview) Could not preview {ItemName}; error: {ErrorMessage}", episode.Name, e.Message);
                }
                if (i < targets.Count - 1)
                    await delayer.Delay(TimeSpan.FromSeconds(2));
            }
            operation.ReportProgress(i + 1, targets.Count);
        }
        operation.Result = incomplete ? OperationState.Partial : OperationState.Succeeded;
        operation.Message = $"Previewed {Seasons(targets.Count)}: {tally.GetValueOrDefault(PreviewRowKind.Change)} to change, " +
            $"{tally.GetValueOrDefault(PreviewRowKind.Reduction)} that would lower progress. Nothing was written." +
            (incomplete ? " Some seasons could not be previewed; see the Jellyfin log." : "");
    }

    private async Task<IReadOnlyList<PreviewRow>> RowsFor(ProviderPass pass, bool applySavedMatches, CancellationToken cancellationToken) {
        // D4: what an apply re-checks. The effective match is read here, not taken from the targets, so a match that
        // existed while applySavedMatches was off is recorded too.
        var season = new PreviewSeason(pass.Tracker.Name, pass.Source,
            matches.GetEffective(userId, pass.Tracker.Name, pass.Source.SourceKey, true)?.RecordedRevision, applySavedMatches,
            WatchedFingerprint.Of(watchState.Watched(pass.Source, userId).Select(episode => episode.Number)));
        if (pass.Failure is { } failure)
            return [season.FromOutcome(pass.Executor.ResolutionFailureOutcome(failure, pass.Source.SearchTitle))];

        SourceResolution resolution = pass.Resolution!;
        IReadOnlyList<PlanStep> steps = await pass.Executor.Plan(resolution, cancellationToken);
        var result = steps.Select(step => step switch {
            PlannedTarget planned => season.FromTarget(planned, pass.Tracker.Capabilities),
            PlannedOutcome outcome => season.FromOutcome(outcome),
            _ => throw new ArgumentOutOfRangeException(nameof(pass), step, "Unknown plan step.")
        }).ToList();
        bool stoppedEarly = steps.Count > 0 && steps[^1] is PlannedOutcome { StopsProvider: true };
        if (resolution is Unresolved { Record: null } silent && !stoppedEarly)
            result.Add(season.Silent(silent));
        return result;
    }

    private static string Cancelled(int done, int total) =>
        $"Cancelled after {done} of {Seasons(total)}. Rows found so far are kept, but this preview can no longer be applied.";

    private static string Seasons(int count) => count == 1 ? "1 season" : $"{count} seasons";
}
