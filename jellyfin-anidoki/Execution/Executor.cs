using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Execution;

/// <summary>
/// Runs one provider's resolution: earlier parts in chain order, then the played target. Reads only what resolution did
/// not, plans each target, writes through <see cref="ITrackerProvider.Apply"/>, and records one outcome per target
/// (two when a multi-call write stopped part way). PR 7 adds the staleness re-check here.
/// </summary>
public sealed class Executor(ITrackerProvider tracker, PlanRules rules, PlanPolicy policy, PlaybackOutcomeCollector? outcomes,
    TimeProvider clock, ILogger logger) {
    private readonly ProviderQuirks _quirks = ProviderQuirks.For(tracker.Name);

    private bool StatusOnly => !tracker.Capabilities.TracksProgress;

    public async Task Execute(SourceResolution resolution, CancellationToken cancellationToken) {
        foreach (var part in resolution.EarlierParts)
            await ExecuteTarget(part, cancellationToken);
        switch (resolution) {
            case Resolved resolved:
                await ExecuteTarget(resolved.Played, cancellationToken);
                break;
            case Unresolved { Record: { } record }:
                Record(record.TargetId, record.Title, OutcomeKind.Unresolved, "resolve");
                break;
        }
    }

    private async Task ExecuteTarget(ResolvedTarget target, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        EntryState? state = target.State ?? await tracker.ReadEntry(target.Entry, cancellationToken);
        if (state == null) {
            logger.LogInformation("({ApiName}) Could not retrieve the matched anime from the provider; not updating", tracker.Name);
            EntryRef entry = target.Entry;
            Record(entry.AlternativeId ?? (entry.Lookup == null ? entry.Id.ToString() : ""), entry.Lookup?.Title ?? "",
                OutcomeKind.Unresolved, "resolve");
            return;
        }

        var decision = Planner.Plan(new PlanInput(target.TargetEpisode, state, rules, policy, tracker.Capabilities, _quirks,
            clock.GetUtcNow().LocalDateTime));
        switch (decision) {
            case Write write:
                await Apply(write, cancellationToken);
                break;
            case NoChange noChange:
                RecordDecision(state, OutcomeKind.NoChange, noChange.Reason);
                break;
            case Skip skip:
                RecordDecision(state, OutcomeKind.Skipped, skip.Reason);
                break;
        }
    }

    private async Task Apply(Write write, CancellationToken cancellationToken) {
        EntryState planned = write.PlannedAgainst;
        logger.LogDebug("({ApiName}) Planned {Change} for {Title}", tracker.Name, write.Change, TitleMatching.Display(planned.Titles));
        bool meaningful = Meaningful(write.Change, planned);
        WriteReceipt? receipt;
        try {
            receipt = await tracker.Apply(planned, write.Change, cancellationToken);
        } catch {
            RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false);
            throw;
        }

        if (receipt == null) {
            logger.LogError("({ApiName}) Could not update anime status", tracker.Name);
            RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false);
            return;
        }

        bool changed = meaningful && Changed(receipt, planned);
        if (receipt.Complete) {
            RecordWrite(planned, changed ? OutcomeKind.Confirmed : OutcomeKind.NoChange, write.Reason, receipt, changed);
            return;
        }

        // the first call was acknowledged and a later one was not: record both, as the per-call writes did
        logger.LogError("({ApiName}) Could not finish updating anime status", tracker.Name);
        RecordWrite(planned, changed ? OutcomeKind.Confirmed : OutcomeKind.NoChange, StatusOnly ? "status" : "progress", receipt, changed);
        RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false);
    }

    /// <summary>Whether the change differs from the state it was planned against (was ObserveAnimeWrite's meaningfulChange).</summary>
    private bool Meaningful(EntryChange change, EntryState planned) {
        if (planned.List is not { } list || change.Status != list.Status)
            return true;
        if (StatusOnly)
            return false;
        bool? rewatchingAfter = change.Rewatch switch {
            RewatchTransition.None => null,
            RewatchTransition.Finish => false,
            _ => true
        };
        // a finish always adds one to the count
        return change.Progress != list.Progress || (rewatchingAfter.HasValue && rewatchingAfter != list.Rewatching) ||
            change.Rewatch == RewatchTransition.Finish;
    }

    /// <summary>Whether the provider acknowledged anything different from the planned-against state (was ObserveWrite's check).</summary>
    private bool Changed(WriteReceipt receipt, EntryState planned) {
        if (planned.List is not { } list)
            return true;
        return (receipt.Progress.HasValue && receipt.Progress != list.Progress) ||
            (receipt.Status.HasValue && receipt.Status != list.Status) ||
            (!StatusOnly && receipt.Rewatching.HasValue && receipt.Rewatching != list.Rewatching) ||
            (!StatusOnly && receipt.RewatchCount.HasValue && receipt.RewatchCount != (list.RewatchCount ?? 0));
    }

    private void RecordWrite(EntryState planned, OutcomeKind kind, string step, WriteReceipt? receipt, bool changed) {
        Status? status = receipt?.Status switch {
            null => null,
            TrackerStatus.Completed when receipt.Rewatching == true => Status.Rewatching,
            { } acknowledged => ApiCallHelpersTrackerProvider.ToClientStatus(acknowledged)
        };
        // write outcomes have always used the main title, else the English one
        Record(TargetId(planned), planned.Titles?.Main ?? planned.Titles?.English ?? "", kind, step, receipt?.Progress, status, changed);
    }

    private void RecordDecision(EntryState state, OutcomeKind kind, string step) =>
        Record(TargetId(state), TitleMatching.Display(state.Titles), kind, step);

    private void Record(string targetId, string title, OutcomeKind kind, string step, int? progress = null, Status? status = null,
        bool changed = false) {
        logger.LogInformation("({ApiName}) {Title}: {Kind} ({Step})", tracker.Name, title, kind, step);
        outcomes?.Record(new ProviderWriteOutcome(tracker.Name, targetId, title, kind, StatusOnly ? null : progress, status, StatusOnly,
            step, changed));
    }

    private static string TargetId(EntryState state) => state.Entry.AlternativeId ?? state.Entry.Id.ToString();
}
