using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

/// <summary>An edit that passed the checks, with the tracker it goes to.</summary>
internal sealed record CheckedEdit(ITrackerProvider Tracker, EntryEditContract Edit, EntryRef Entry, string Title);

/// <summary>Whether an edit request may run.</summary>
internal abstract record EditCheck {
    public sealed record Ready(string SeriesName, IReadOnlyList<CheckedEdit> Edits) : EditCheck;

    /// <summary>The item is not one the user can see.</summary>
    public sealed record NotFound : EditCheck;

    /// <summary>An entry the user's matches for the series do not point at.</summary>
    public sealed record Forbidden : EditCheck;

    public sealed record Invalid(string Message) : EditCheck;
}

/// <summary>
/// D4: a pending popup edit becomes an explicit entry edit. Each one re-reads the entry and writes only if the tracker
/// still says what the popup showed, so a change made elsewhere is never overwritten. It may lower progress: the user asked.
/// </summary>
public sealed class EntryEditor(ISeriesItems items, SeriesProgressReader reader, IMatchTrackers trackers, ILogger<EntryEditor> logger) {
    public const string StaleStep = "edit-stale";

    /// <summary>Checks every edit before anything runs: the tracker is linked, the values fit, and the entry is the user's.</summary>
    internal async Task<EditCheck> Check(Guid userId, EditRequest request, bool applySavedMatches, CancellationToken cancellationToken) {
        if (items.Find(request.ItemId, userId) is not { } item)
            return new EditCheck.NotFound();
        if (request.Edits is not { Count: > 0 })
            return new EditCheck.Invalid("Choose at least one change to save.");
        if (request.Edits.GroupBy(edit => (edit.Provider, edit.EntryId, edit.AlternativeId)).Any(group => group.Count() > 1))
            return new EditCheck.Invalid("Each entry can be changed once per save.");

        IReadOnlyList<LibrarySource> sources = reader.Sources(item, userId);
        var ready = new List<CheckedEdit>();
        foreach (var group in request.Edits.GroupBy(edit => edit.Provider)) {
            if (trackers.ForOwner(userId, group.Key) is not { } tracker)
                return new EditCheck.Invalid($"{group.Key} is not linked.");
            // Ownership: only the entries the user's own matches for this series point at.
            var owned = new Dictionary<EntryRef, (MatchTarget Target, EntryState? State)>();
            foreach (LibrarySource source in sources) {
                SeasonMatch season = await reader.Resolve(source, userId, tracker, applySavedMatches, cancellationToken);
                foreach (MatchTarget target in season.Match?.Targets ?? [])
                    owned.TryAdd(target.Entry, (target, season.Entries.GetValueOrDefault(target.Entry)));
            }
            foreach (EntryEditContract edit in group) {
                var found = owned.FirstOrDefault(entry => entry.Key.Id == edit.EntryId && entry.Key.AlternativeId == edit.AlternativeId);
                if (found.Key == null)
                    return new EditCheck.Forbidden();
                if (Problem(edit, found.Value.State, tracker.Capabilities) is { } problem)
                    return new EditCheck.Invalid(problem);
                EntryEditContract kept = tracker.Capabilities.SupportsScore ? edit : edit with { Score = null, ClearScore = false };
                ready.Add(new CheckedEdit(tracker, kept, found.Key, found.Value.Target.Title));
            }
        }
        return new EditCheck.Ready(item.Name, ready);
    }

    internal static string? Problem(EntryEditContract edit, EntryState? state, ProviderCapabilities capabilities) {
        if (!Enum.IsDefined(edit.Status))
            return "Choose a status.";
        // D11: rewatches are not edited here, and a plain write would end the rewatch on the tracker.
        if (state?.List?.Rewatching == true)
            return "This entry is being rewatched. Change it on the tracker for now.";
        if (edit.Progress < 0)
            return "Episodes watched cannot be negative.";
        int total = state?.TotalEpisodes ?? 0;
        if (total > 0 && edit.Progress > total)
            return $"Episodes watched cannot be more than {total}.";
        if (!capabilities.SupportsScore)
            return null;
        if (edit.Score != null && edit.ClearScore)
            return "Either set a rating or clear it, not both.";
        if (edit.Score is < 1 or > 100)
            return "The rating is outside the tracker's scale.";
        return null;
    }

    internal async Task Run(IReadOnlyList<CheckedEdit> edits, OperationContext operation) {
        int done = 0;
        foreach (CheckedEdit edit in edits) {
            operation.CancellationToken.ThrowIfCancellationRequested();
            await Edit(edit, operation);
            operation.ReportProgress(++done, edits.Count);
        }
    }

    internal async Task Edit(CheckedEdit edit, OperationContext operation) {
        ITrackerProvider tracker = edit.Tracker;
        string targetId = edit.Entry.AlternativeId ?? edit.Entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EntryEditContract wanted = edit.Edit;
        var change = new EntryChange(wanted.Progress, wanted.Status, Score: wanted.Score, ClearScore: wanted.ClearScore);
        EntryState? current = null;
        try {
            current = await tracker.ReadEntry(edit.Entry, operation.CancellationToken);
            if (current == null) {
                logger.LogInformation("({Provider}) Could not find {Title} to edit", tracker.Name, edit.Title);
                operation.Record(Outcome(tracker, targetId, edit.Title, OutcomeKind.Unresolved, "resolve", null), Detail(change, null));
                return;
            }
            if (!Matches(current, wanted.Shown, tracker.Capabilities)) {
                logger.LogInformation("({Provider}) Skipped the edit to {Title} because it changed on the tracker", tracker.Name, edit.Title);
                operation.Record(Outcome(tracker, targetId, edit.Title, OutcomeKind.Skipped, StaleStep, null), Detail(change, current));
                return;
            }
            WriteReceipt? receipt = await tracker.Apply(current, change, operation.CancellationToken);
            if (receipt is not { Complete: true }) {
                logger.LogWarning("({Provider}) Could not confirm the edit to {Title}", tracker.Name, edit.Title);
                operation.Record(Outcome(tracker, targetId, edit.Title, OutcomeKind.Unconfirmed, "edit", receipt), Detail(change, current));
                return;
            }
            logger.LogInformation("({Provider}) Saved the edit to {Title}", tracker.Name, edit.Title);
            operation.Record(Outcome(tracker, targetId, edit.Title, OutcomeKind.Confirmed, "edit", receipt), Detail(change, current));
        } catch (TrackerFailureException e) {
            var (kind, step) = Executor.FailureKindOutcome(e.Kind);
            logger.LogWarning("({Provider}) Could not save the edit to {Title}: {ErrorMessage}", tracker.Name, edit.Title, e.Message);
            operation.Record(Outcome(tracker, targetId, edit.Title, kind, step, null), Detail(change, current));
        }
    }

    /// <summary>Whether the tracker still says what the popup showed. A score counts only where the tracker has scores.</summary>
    internal static bool Matches(EntryState current, ShownStateContract shown, ProviderCapabilities capabilities) =>
        current.List?.Status == shown.Status
        && (current.List?.Progress ?? 0) == (shown.Progress ?? 0)
        && (!capabilities.SupportsScore || current.List?.Score == shown.Score);

    /// <summary>The results of an edit operation, one per entry, from its recorded actions.</summary>
    internal static EditOutcomeContract Results(OperationView view, IReadOnlyList<RecordedAction> actions) =>
        new(view.OperationId, OperationStates.IsFinished(view.State), actions.Select(action => new EditResultContract(action.Provider, action.TargetId,
            action.Outcome switch {
                OutcomeKind.Confirmed or OutcomeKind.NoChange => EditResultKind.Saved,
                OutcomeKind.Skipped when action.Step == StaleStep => EditResultKind.Stale,
                OutcomeKind.Unconfirmed => EditResultKind.Unconfirmed,
                OutcomeKind.Unresolved => EditResultKind.NotFound,
                _ => EditResultKind.Failed
            },
            action.Outcome == OutcomeKind.Skipped && action.PlannedAgainst is { } against
                ? new ShownStateContract(against.List?.Status, against.List?.Progress, against.List?.Score) : null,
            action.Outcome == OutcomeKind.Failed ? action.Step : null)).ToList());

    private static ProviderWriteOutcome Outcome(ITrackerProvider tracker, string targetId, string title, OutcomeKind kind, string step, WriteReceipt? receipt) =>
        new(tracker.Name, targetId, title, kind, receipt?.Progress, receipt?.Status is { } status ? TrackerProvider.ToClientStatus(status) : null, false, step,
            kind == OutcomeKind.Confirmed);

    private static OutcomeDetail Detail(EntryChange change, EntryState? against) => new(null, null, change.Progress, null, null, change, against);
}
