using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Preview;

/// <summary>
/// Writes the selected rows of a Jellyfin → Trackers preview, each exactly as previewed or not at all (D4). Each row is
/// checked first: its tracker still linked, the saved-match setting and the effective match unchanged, the season's
/// watched episodes unchanged. Then <see cref="Executor.ApplyPlanned"/> re-reads the entry in <see cref="ApplyMode.Strict"/>
/// mode and refuses it if it moved. Nothing is re-planned. Runs inside an operation, which already holds the user's sync gate.
/// </summary>
public sealed class PreviewApply(
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor,
    IMemoryCache memoryCache,
    IAsyncDelayer delayer,
    MatchStore matches,
    ISeasonWatchState watchState,
    Guid userId,
    IReadOnlyList<PreviewRow> selected) {
    /// <summary>Something the row was planned from changed; the outcome's note says what.</summary>
    public const string Stale = "preview-stale";

    /// <summary>D8: the apply was cancelled before this row.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>D8: this row's tracker refused or rate-limited an earlier row, so it was not asked.</summary>
    public const string ProviderStopped = "provider-stopped";

    private readonly ILogger<PreviewApply> _logger = loggerFactory.CreateLogger<PreviewApply>();

    /// <summary>Tests set this to substitute an in-memory tracker; production leaves it null so the real client is built.</summary>
    internal Func<ApiName, UserConfig, ITrackerClient>? ClientOverride { get; set; }

    public async Task Run(OperationContext operation) {
        UserConfig? user = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(config => config.UserId == userId);
        bool applySavedMatches = Plugin.Instance?.PluginConfiguration.applySavedMatches ?? false;
        var clients = new TrackerClientFactory(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache,
            delayer, _logger) { Override = ClientOverride };
        var trackers = new Dictionary<ApiName, ITrackerProvider?>();
        var fingerprints = new Dictionary<Guid, string?>();
        var stopped = new HashSet<ApiName>();
        var results = new List<ProviderWriteOutcome>();
        operation.ReportProgress(0, selected.Count);
        for (int i = 0; i < selected.Count; i++) {
            PreviewRow row = selected[i];
            // The same wait between seasons as Run sync, so a large apply does not trip a tracker's rate limit.
            if (i > 0 && row.SourceKey != selected[i - 1].SourceKey)
                await delayer.Delay(TimeSpan.FromSeconds(2));
            if (operation.CancellationToken.IsCancellationRequested)
                StopCancelled(operation, selected, i, _logger);
            await ApplyCounted(operation, results, () => ApplyRow(operation, row, user, applySavedMatches, clients, trackers, fingerprints, stopped));
            operation.ReportProgress(i + 1, selected.Count);
        }
        operation.Message = Summary(results);
        _logger.LogInformation("Applied {Count} selected preview rows for user {UserId}", selected.Count, userId);
    }

    private async Task ApplyRow(OperationContext operation, PreviewRow row, UserConfig? user, bool applySavedMatches,
        TrackerClientFactory clients, Dictionary<ApiName, ITrackerProvider?> trackers, Dictionary<Guid, string?> fingerprints,
        HashSet<ApiName> stopped) {
        if (stopped.Contains(row.Provider)) {
            Refuse(operation, row, ProviderStopped, null, _logger);
            return;
        }
        if (StaleBecause(row, user, applySavedMatches, fingerprints) is { } why) {
            Refuse(operation, row, Stale, why, _logger);
            return;
        }
        // StaleBecause refuses a row whose user or link is gone, so the user is known from here on.
        if (!trackers.TryGetValue(row.Provider, out ITrackerProvider? tracker)) {
            tracker = clients.Create(row.Provider, user!) is { } client ? clients.Adapt(row.Provider, client) : null;
            trackers[row.Provider] = tracker;
        }
        if (tracker == null) {
            Refuse(operation, row, Stale, "not-configured", _logger);
            return;
        }
        var executor = new Executor(tracker, new PlanRules(user!.PlanToWatchOnly, user.RewatchCompleted, user.AddUnlistedAnime), PlanPolicy.ManualSync,
            new PlaybackOutcomeCollector((outcome, detail) => operation.Record(outcome, Tagged(detail, row))), TimeProvider.System, _logger,
            new ActionSource(row.SourceItem, row.SourceEpisode ?? SyncSource.MovieProgress));
        // D8: a row being written is finished even if the apply is cancelled meanwhile, so the write is not cut off.
        if (!await executor.ApplyPlanned(Planned(row), ApplyMode.Strict, CancellationToken.None))
            stopped.Add(row.Provider);
    }

    /// <returns>What no longer matches the preview (the outcome's note), or null when everything still does.</returns>
    private string? StaleBecause(PreviewRow row, UserConfig? user, bool applySavedMatches, Dictionary<Guid, string?> fingerprints) {
        if (user?.UserApiAuth?.Any(auth => auth.Name == row.Provider) != true)
            return "not-linked";
        if (applySavedMatches != row.ApplySavedMatches)
            return "match-setting";
        if (matches.GetEffective(userId, row.Provider, row.SourceKey, true)?.RecordedRevision != row.MatchRevision)
            return "match";
        if (!fingerprints.TryGetValue(row.SourceItem, out string? fingerprint)) {
            fingerprint = Fingerprint(row.SourceItem);
            fingerprints[row.SourceItem] = fingerprint;
        }
        if (fingerprint == null)
            return "item-missing";
        return fingerprint == row.WatchedFingerprint ? null : "watched";
    }

    /// <summary>The same fingerprint the preview stored, from the same item; null when the item is gone.</summary>
    private string? Fingerprint(Guid item) =>
        libraryManager.GetItemById(item) is Video video && SyncSource.From(video) is { } source
            ? WatchedFingerprint.Of(watchState.Watched(source, userId).Select(episode => episode.Number))
            : null;

    /// <summary>The write as previewed, rebuilt from the row. Only selectable rows reach here, and they carry both snapshots.</summary>
    internal static PlannedTarget Planned(PreviewRow row) {
        EntryState read = row.PlannedAgainst!;
        EntryChange change = row.PlannedChange!;
        var target = new ResolvedTarget(read.Entry, row.TargetEpisode ?? change.Progress, TargetRole.Played, row.TargetTitle,
            MatchRevision: row.MatchRevision);
        var write = new Write(change, row.Reason, Planner.AsPlanned(read, ProviderQuirks.For(row.Provider)));
        return new PlannedTarget(new ActionTrace(target, row.ResolvedBy ?? ResolvedBy.Ids, row.ResolutionNote), read, write);
    }

    /// <summary>
    /// D8: records every row from <paramref name="reached"/> on as not applied, says how far the apply got, and stops.
    /// </summary>
    internal static void StopCancelled(OperationContext operation, IReadOnlyList<PreviewRow> selected, int reached, ILogger logger) {
        // Every row not reached is recorded, so the result lists exactly what was and was not applied.
        foreach (PreviewRow unreached in selected.Skip(reached))
            Refuse(operation, unreached, Cancelled, null, logger);
        operation.Message = $"Stopped after {reached} of {selected.Count}. Updates already sent were kept.";
        operation.CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Records a selected row as not applied, with the step and the note that say why.</summary>
    internal static void Refuse(OperationContext operation, PreviewRow row, string step, string? note, ILogger logger) {
        logger.LogInformation("({Provider}) {Title}: {Kind} ({Step})", row.Provider, row.TargetTitle, OutcomeKind.Skipped, step);
        operation.Record(new ProviderWriteOutcome(row.Provider, row.TargetId, row.TargetTitle, OutcomeKind.Skipped, null, null,
            !ProviderCapabilities.For(row.Provider).TracksProgress, step, false), Detail(row, note));
    }

    internal static OutcomeDetail Detail(PreviewRow row, string? note) =>
        new(row.SourceItem, row.SourceEpisode, row.TargetEpisode, row.ResolvedBy, note, row.PlannedChange, row.PlannedAgainst,
            row.MatchRevision, row.Seq);

    private static OutcomeDetail Tagged(OutcomeDetail? detail, PreviewRow row) =>
        detail == null ? Detail(row, null) : detail with { PreviewSeq = row.Seq };

    /// <summary>
    /// One line counting each row's result, in the words the preview shows for them: <c>3 updated · 1 changed since the
    /// preview · 1 failed</c>. Null when no row was reached.
    /// </summary>
    /// <param name="results">The last outcome recorded for each row.</param>
    /// <param name="inbound">Inbound rows are marked played rather than updated.</param>
    internal static string? Summary(IEnumerable<ProviderWriteOutcome> results, bool inbound = false) {
        IReadOnlyList<ProviderWriteOutcome> rows = results.ToList();
        (string Label, Func<ProviderWriteOutcome, bool> Counts)[] parts = [
            (inbound ? "marked played" : "updated", outcome => outcome.Kind == OutcomeKind.Confirmed),
            ("already up to date", outcome => outcome.Kind == OutcomeKind.NoChange),
            ("changed since the preview", outcome => outcome is { Kind: OutcomeKind.Skipped, Step: Stale }),
            ("not reached", outcome => outcome is { Kind: OutcomeKind.Skipped, Step: Cancelled }),
            ("not tried", outcome => outcome is { Kind: OutcomeKind.Skipped, Step: ProviderStopped }),
            ("skipped", outcome => outcome is { Kind: OutcomeKind.Skipped, Step: not (Stale or Cancelled or ProviderStopped) }),
            ("not found", outcome => outcome.Kind == OutcomeKind.Unresolved),
            ("failed", outcome => outcome.Kind == OutcomeKind.Failed),
            ("not confirmed", outcome => outcome.Kind == OutcomeKind.Unconfirmed)
        ];
        string[] counted = parts.Select(part => (Count: rows.Count(part.Counts), part.Label)).Where(part => part.Count > 0)
            .Select(part => $"{part.Count} {part.Label}").ToArray();
        return counted.Length == 0 ? null : string.Join(" · ", counted);
    }

    /// <summary>Runs one row's apply and keeps the last outcome it recorded, which is that row's result.</summary>
    internal static async Task ApplyCounted(OperationContext operation, List<ProviderWriteOutcome> results, Func<Task> apply) {
        int before = operation.Outcomes.Count;
        await apply();
        IReadOnlyList<ProviderWriteOutcome> after = operation.Outcomes;
        if (after.Count > before)
            results.Add(after[^1]);
    }
}
