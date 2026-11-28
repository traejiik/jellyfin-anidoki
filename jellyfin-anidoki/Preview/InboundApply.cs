using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Preview;

/// <summary>
/// Marks the selected seasons of a Trackers → Jellyfin preview as played, each exactly as previewed or not at all (D4). The
/// tracker list is read once. A row is marked only while its tracker entry still reads as it did, its season and episodes are
/// still in the library without the ignore tag, and the season's played episodes are unchanged; it is then marked through the
/// same step as Run sync from trackers. Nothing is re-planned. Runs inside an operation, which already holds the user's sync gate.
/// </summary>
/// <param name="sync">Reads the list of the rows' tracker (Completed and Watching) and marks seasons.</param>
public sealed class InboundApply(
    Sync sync,
    ILibraryManager libraryManager,
    IUserManager users,
    IUserDataManager userData,
    ILoggerFactory loggerFactory,
    Guid userId,
    IReadOnlyList<PreviewRow> selected) {
    private const string Unreadable = "The tracker list could not be read, so nothing was marked.";

    private readonly ILogger<InboundApply> _logger = loggerFactory.CreateLogger<InboundApply>();

    public async Task Run(OperationContext operation) {
        operation.ReportProgress(0, selected.Count);
        User? user = users.GetUserById(userId);
        UserConfig? config = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(item => item.UserId == userId);
        // An inbound preview reads one tracker, so every row names the same one.
        ApiName provider = selected[0].Provider;
        if (user == null || config?.UserApiAuth?.Any(auth => auth.Name == provider) != true) {
            RefuseAll(operation, PreviewApply.Stale, "not-linked");
            operation.Message = PreviewApply.Summary(operation.Outcomes, inbound: true);
            return;
        }

        List<Anime>? list = await sync.ReadList(userId);
        if (list == null) {
            RefuseAll(operation, PreviewApply.ProviderStopped, null);
            operation.Result = OperationState.Failed;
            operation.Message = Unreadable;
            return;
        }

        Dictionary<int, InboundEntry> entries = list.DistinctBy(anime => anime.Id)
            .ToDictionary(anime => anime.Id, anime => InboundRows.Entry(anime, provider));
        var results = new List<ProviderWriteOutcome>();
        for (int i = 0; i < selected.Count; i++) {
            if (operation.CancellationToken.IsCancellationRequested)
                PreviewApply.StopCancelled(operation, selected, i, _logger);
            PreviewRow row = selected[i];
            await PreviewApply.ApplyCounted(operation, results, () => {
                ApplyRow(operation, row, user, entries);
                return Task.CompletedTask;
            });
            operation.ReportProgress(i + 1, selected.Count);
        }
        operation.Message = PreviewApply.Summary(results, inbound: true);
        _logger.LogInformation("({Provider}) Applied {Count} selected preview rows for user {UserId}", provider, selected.Count, userId);
    }

    private void ApplyRow(OperationContext operation, PreviewRow row, User user, Dictionary<int, InboundEntry> entries) {
        // Only selectable rows reach here, and an inbound one carries both its mark and its tracker entry.
        InboundChange mark = row.PlannedMark!;
        Season? season = Fresh(mark.SeasonItem);
        List<Episode> children = season?.Children.OfType<Episode>().ToList() ?? [];
        List<Episode> episodes = mark.Episodes.Select(id => children.FirstOrDefault(episode => episode.Id == id)).OfType<Episode>().ToList();
        if (StaleBecause(row, user, entries, season, episodes) is { } why) {
            PreviewApply.Refuse(operation, row, PreviewApply.Stale, why, _logger);
            return;
        }

        sync.Mark(user, season!, episodes, mark.PlayedDate, mark.MarksSeason);
        _logger.LogInformation("({Provider}) {Title}: {Kind} ({Step})", row.Provider, row.SourceTitle, OutcomeKind.Confirmed, row.Reason);
        operation.Record(new ProviderWriteOutcome(row.Provider, row.TargetId, row.TargetTitle, OutcomeKind.Confirmed, row.SourceEpisode, null,
            false, row.Reason, true), PreviewApply.Detail(row, null));
    }

    /// <returns>What no longer matches the preview (the outcome's note), or null when everything still does.</returns>
    private string? StaleBecause(PreviewRow row, User user, Dictionary<int, InboundEntry> entries, Season? season, List<Episode> episodes) {
        InboundEntry was = row.TrackerEntry!;
        // D4 compares status, progress and finish date; a renamed entry is still the same entry.
        if (!entries.TryGetValue(was.Id, out InboundEntry? now) || now with { Title = was.Title } != was)
            return "tracker";
        if (season == null || episodes.Count != row.PlannedMark!.Episodes.Count)
            return "item-missing";
        if (SyncHelper.MediaShouldBeIgnored(season) || episodes.Any(SyncHelper.HasIgnoreTag))
            return "ignored";
        string fingerprint = InboundRows.Fingerprint(season, item => userData.GetUserData(user, item)?.Played == true);
        return fingerprint == row.WatchedFingerprint ? null : "watched";
    }

    /// <summary>
    /// The season as stored now, with its episodes loaded afresh, as the preview and Run sync load theirs. Not
    /// <c>GetItemById</c>: its cached season keeps the episodes, and their played state, from when it was first loaded.
    /// </summary>
    private Season? Fresh(Guid seasonId) =>
        libraryManager.GetItemList(new InternalItemsQuery { ItemIds = [seasonId] }).OfType<Season>().FirstOrDefault();

    private void RefuseAll(OperationContext operation, string step, string? note) {
        foreach (PreviewRow row in selected)
            PreviewApply.Refuse(operation, row, step, note, _logger);
    }
}
