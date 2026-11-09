using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Execution;

/// <summary>The Jellyfin side of an action, for its history row.</summary>
public sealed record ActionSource(Guid ItemId, int Episode);

/// <summary>
/// Runs one provider's resolution: earlier parts in chain order, then the played target. Reads only what resolution did
/// not, plans each target, writes through <see cref="ITrackerProvider.Apply"/>, and records one outcome per target
/// (two when a multi-call write stopped part way). A read older than <see cref="StaleAfter"/> is repeated before writing from it.
/// <see cref="Plan"/> does the reading and planning alone and records nothing; <see cref="ApplyPlanned"/> writes one planned target.
/// </summary>
public sealed class Executor(ITrackerProvider tracker, PlanRules rules, PlanPolicy policy, PlaybackOutcomeCollector? outcomes,
    TimeProvider clock, ILogger logger, ActionSource? source = null) {
    /// <summary>A read older than this is read again before anything is written from it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private readonly ProviderQuirks _quirks = ProviderQuirks.For(tracker.Name);

    // The plan of the target being written, so a failed write can record it. Null until a write starts.
    private EntryChange? _plannedChange;
    private EntryState? _plannedRead;

    private bool StatusOnly => !tracker.Capabilities.TracksProgress;

    public async Task Execute(SourceResolution resolution, CancellationToken cancellationToken) {
        // Target by target, not Plan then apply: the played target is not read once an earlier part's write stopped the provider.
        foreach (ActionTrace trace in Traces(resolution)) {
            if (!await ExecuteTarget(trace, cancellationToken))
                return;
        }

        if (ResolutionOutcome(resolution) is { } outcome)
            Emit(outcome);
    }

    /// <summary>Reads and plans every target in the order <see cref="Execute"/> would, without writing or recording anything.</summary>
    /// <returns>One step per target, then what the resolution itself records; it ends early where <see cref="Execute"/> would stop.</returns>
    public async Task<IReadOnlyList<PlanStep>> Plan(SourceResolution resolution, CancellationToken cancellationToken) {
        var steps = new List<PlanStep>();
        foreach (ActionTrace trace in Traces(resolution)) {
            cancellationToken.ThrowIfCancellationRequested();
            PlanStep step = await PlanTarget(trace, cancellationToken);
            steps.Add(step);
            if (step is PlannedOutcome { StopsProvider: true })
                return steps;
        }

        if (ResolutionOutcome(resolution) is { } outcome)
            steps.Add(outcome);
        return steps;
    }

    /// <summary>Writes one planned target and records its outcome.</summary>
    /// <returns>False when the provider should not be asked for anything else.</returns>
    public async Task<bool> ApplyPlanned(PlannedTarget planned, ApplyMode mode, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return await ApplyTarget(planned, mode, cancellationToken);
    }

    /// <summary>A failure before any target was known; it covers the whole provider for this source.</summary>
    public void RecordResolutionFailure(TrackerFailureException failure, string title) => Emit(ResolutionFailureOutcome(failure, title));

    /// <summary>What <see cref="RecordResolutionFailure"/> records, for a caller that collects outcomes instead.</summary>
    public PlannedOutcome ResolutionFailureOutcome(TrackerFailureException failure, string title) {
        var (kind, step) = FailureKindOutcome(failure.Kind);
        return new PlannedOutcome(Outcome("", title, kind, step), new OutcomeDetail(source?.ItemId, source?.Episode, null, null,
            failure.Kind == TrackerFailureKind.NotFound ? "not-found" : null, null, null), true);
    }

    private static IEnumerable<ActionTrace> Traces(SourceResolution resolution) {
        // Only a saved match says why an earlier part is a target; otherwise it came from the sequel chain.
        ResolvedBy earlierBy = resolution is Resolved { By: ResolvedBy.SharedMatch or ResolvedBy.PersonalMatch } matched ? matched.By : ResolvedBy.SequelChain;
        foreach (var part in resolution.EarlierParts)
            yield return new ActionTrace(part, earlierBy, resolution.Note);
        if (resolution is Resolved resolved)
            yield return new ActionTrace(resolved.Played, resolved.By, resolved.Note);
    }

    private PlannedOutcome? ResolutionOutcome(SourceResolution resolution) => resolution switch {
        Unresolved { Record: { } record } unresolved => new PlannedOutcome(Outcome(record.TargetId, record.Title, OutcomeKind.Unresolved, "resolve"),
            new OutcomeDetail(source?.ItemId, source?.Episode, null, null, unresolved.Note ?? unresolved.Reason, null, null), false),
        Ignored ignored => new PlannedOutcome(Outcome("", ignored.Title, OutcomeKind.Skipped, "match-ignored"),
            new OutcomeDetail(source?.ItemId, source?.Episode, null, ignored.By, null, null, null, ignored.MatchRevision), false),
        _ => null
    };

    /// <returns>False when the provider should not be asked for anything else.</returns>
    private async Task<bool> ExecuteTarget(ActionTrace trace, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        PlanStep step = await PlanTarget(trace, cancellationToken);
        if (step is PlannedOutcome outcome) {
            Emit(outcome);
            return !outcome.StopsProvider;
        }

        return await ApplyTarget((PlannedTarget)step, ApplyMode.Automatic, cancellationToken);
    }

    private async Task<PlanStep> PlanTarget(ActionTrace trace, CancellationToken cancellationToken) {
        EntryState? state;
        try {
            state = trace.Target.State ?? await tracker.ReadEntry(trace.Target.Entry, cancellationToken);
        } catch (TrackerFailureException failure) {
            return FailureOutcome(failure, trace, null, null);
        }

        return state == null ? UnreadableOutcome(trace) : new PlannedTarget(trace, state, Decide(trace.Target, state));
    }

    /// <returns>False when the provider should not be asked for anything else.</returns>
    private async Task<bool> ApplyTarget(PlannedTarget planned, ApplyMode mode, CancellationToken cancellationToken) {
        _plannedChange = null;
        _plannedRead = null;
        try {
            await CheckAndWrite(planned, mode, cancellationToken);
            return true;
        } catch (TrackerFailureException failure) {
            PlannedOutcome outcome = FailureOutcome(failure, planned.Trace, _plannedChange, _plannedRead);
            Emit(outcome);
            return !outcome.StopsProvider;
        }
    }

    private async Task CheckAndWrite(PlannedTarget planned, ApplyMode mode, CancellationToken cancellationToken) {
        ActionTrace trace = planned.Trace;
        ResolvedTarget target = trace.Target;
        EntryState state = planned.Read;
        PlanDecision decision = planned.Decision;
        if (decision is Write && mode == ApplyMode.Strict) {
            EntryState? current = await tracker.ReadEntry(target.Entry, cancellationToken);
            if (current == null) {
                Emit(UnreadableOutcome(trace));
                return;
            }

            if (!SameEntry(state, current)) {
                RecordDecision(current, OutcomeKind.Skipped, "preview-stale", trace);
                return;
            }

            state = current;
        } else if (decision is Write && clock.GetUtcNow() - state.ReadAt > StaleAfter) {
            EntryState? fresh = await tracker.ReadEntry(target.Entry, cancellationToken);
            if (fresh == null) {
                Emit(UnreadableOutcome(trace));
                return;
            }

            bool moved = !SameEntry(state, fresh);
            logger.LogDebug("({ApiName}) Read again before writing a stale plan; entry changed: {Moved}", tracker.Name, moved);
            state = fresh;
            decision = Decide(target, fresh);
            if (moved && decision is Write) {
                // Something else is updating this entry right now, so make sure it has settled before writing over it.
                EntryState? settled = await tracker.ReadEntry(target.Entry, cancellationToken);
                if (settled == null) {
                    Emit(UnreadableOutcome(trace));
                    return;
                }

                if (!SameEntry(fresh, settled)) {
                    RecordDecision(settled, OutcomeKind.Failed, "stale", trace);
                    return;
                }
            }
        }

        switch (decision) {
            case Write write:
                await Apply(write, state, trace, cancellationToken);
                break;
            case NoChange noChange:
                RecordDecision(state, OutcomeKind.NoChange, noChange.Reason, trace);
                break;
            case Skip skip:
                RecordDecision(state, OutcomeKind.Skipped, skip.Reason, trace);
                break;
        }
    }

    // Titles and ReadAt are left out: a re-read always has a new ReadAt, and titles do not affect a plan.
    private static bool SameEntry(EntryState first, EntryState second) =>
        first.TotalEpisodes == second.TotalEpisodes && first.List == second.List;

    private PlanDecision Decide(ResolvedTarget target, EntryState state) =>
        Planner.Plan(new PlanInput(target.TargetEpisode, state, rules, policy, tracker.Capabilities, _quirks, clock.GetUtcNow().LocalDateTime));

    private async Task Apply(Write write, EntryState read, ActionTrace trace, CancellationToken cancellationToken) {
        EntryState planned = write.PlannedAgainst;
        logger.LogDebug("({ApiName}) Planned {Change} for {Title}", tracker.Name, write.Change, TitleMatching.Display(planned.Titles));
        _plannedChange = write.Change;
        _plannedRead = read;
        OutcomeDetail detail = Detail(trace, write.Change, read);
        bool meaningful = Meaningful(write.Change, planned);
        WriteReceipt? receipt;
        try {
            receipt = await tracker.Apply(planned, write.Change, cancellationToken);
        } catch (Exception error) when (error is not TrackerFailureException) {
            RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false, detail);
            throw;
        }

        if (receipt == null) {
            logger.LogError("({ApiName}) Could not update anime status", tracker.Name);
            RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false, detail);
            return;
        }

        bool changed = meaningful && Changed(receipt, planned, write.Change);
        if (receipt.Complete) {
            RecordWrite(planned, changed ? OutcomeKind.Confirmed : OutcomeKind.NoChange, write.Reason, receipt, changed, detail);
            return;
        }

        // the first call was acknowledged and a later one was not: record both, as the per-call writes did
        logger.LogError("({ApiName}) Could not finish updating anime status", tracker.Name);
        RecordWrite(planned, changed ? OutcomeKind.Confirmed : OutcomeKind.NoChange, StatusOnly ? "status" : "progress", receipt, changed, detail);
        RecordWrite(planned, OutcomeKind.Unconfirmed, write.Reason, null, false, detail);
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
    private bool Changed(WriteReceipt receipt, EntryState planned, EntryChange change) {
        if (planned.List is not { } list)
            return true;
        // Kitsu and Shikimori accept a count without echoing it, and a finished write always moves the count.
        if (receipt is { Complete: true, RewatchCount: null } && change.Rewatch == RewatchTransition.Finish)
            return true;
        return (receipt.Progress.HasValue && receipt.Progress != list.Progress) ||
            (receipt.Status.HasValue && receipt.Status != list.Status) ||
            (!StatusOnly && receipt.Rewatching.HasValue && receipt.Rewatching != list.Rewatching) ||
            (!StatusOnly && receipt.RewatchCount.HasValue && receipt.RewatchCount != (list.RewatchCount ?? 0));
    }

    private OutcomeDetail Detail(ActionTrace trace, EntryChange? change = null, EntryState? read = null, string? note = null) =>
        // Titles are already on the row, and a long synonym list would repeat on every action.
        new(source?.ItemId, source?.Episode, trace.Target.TargetEpisode, trace.By, note ?? trace.Note, change, read == null ? null : read with { Titles = null }, trace.Target.MatchRevision);

    private PlannedOutcome UnreadableOutcome(ActionTrace trace) {
        logger.LogInformation("({ApiName}) Could not retrieve the matched anime from the provider; not updating", tracker.Name);
        EntryRef entry = trace.Target.Entry;
        return new PlannedOutcome(Outcome(entry.AlternativeId ?? (entry.Lookup == null ? entry.Id.ToString() : ""), entry.Lookup?.Title ?? "",
            OutcomeKind.Unresolved, "resolve"), Detail(trace, note: "not-found"), false);
    }

    /// <param name="change">The change being written when it failed; null when it failed while reading.</param>
    /// <param name="read">The state that change was planned from; null when it failed while reading.</param>
    private PlannedOutcome FailureOutcome(TrackerFailureException failure, ActionTrace trace, EntryChange? change, EntryState? read) {
        EntryRef entry = trace.Target.Entry;
        string targetId = entry.AlternativeId ?? (entry.Lookup == null ? entry.Id.ToString() : "");
        var (kind, step) = FailureKindOutcome(failure.Kind);
        string? note = failure.Kind == TrackerFailureKind.NotFound ? "not-found" : null;
        // Only a missing entry leaves the provider usable.
        return new PlannedOutcome(Outcome(targetId, trace.Target.Title, kind, step), Detail(trace, change, read, note),
            failure.Kind != TrackerFailureKind.NotFound);
    }

    private static (OutcomeKind Kind, string Step) FailureKindOutcome(TrackerFailureKind failure) => failure switch {
        TrackerFailureKind.NotFound => (OutcomeKind.Unresolved, "resolve"),
        TrackerFailureKind.Unauthorized => (OutcomeKind.Failed, "unauthorized"),
        TrackerFailureKind.RateLimited => (OutcomeKind.Failed, "rate-limited"),
        TrackerFailureKind.Transient => (OutcomeKind.Failed, "transient"),
        _ => (OutcomeKind.Failed, "rejected")
    };

    private void RecordWrite(EntryState planned, OutcomeKind kind, string step, WriteReceipt? receipt, bool changed, OutcomeDetail detail) {
        Status? status = receipt?.Status switch {
            null => null,
            TrackerStatus.Completed when receipt.Rewatching == true => Status.Rewatching,
            { } acknowledged => TrackerProvider.ToClientStatus(acknowledged)
        };
        // write outcomes have always used the main title, else the English one
        Emit(Outcome(TargetId(planned), planned.Titles?.Main ?? planned.Titles?.English ?? "", kind, step, receipt?.Progress, status, changed), detail);
    }

    private void RecordDecision(EntryState state, OutcomeKind kind, string step, ActionTrace trace) =>
        Emit(Outcome(TargetId(state), TitleMatching.Display(state.Titles), kind, step), Detail(trace, read: state));

    private ProviderWriteOutcome Outcome(string targetId, string title, OutcomeKind kind, string step, int? progress = null, Status? status = null,
        bool changed = false) =>
        new(tracker.Name, targetId, title, kind, StatusOnly ? null : progress, status, StatusOnly, step, changed);

    private void Emit(PlannedOutcome planned) => Emit(planned.Outcome, planned.Detail);

    private void Emit(ProviderWriteOutcome outcome, OutcomeDetail? detail) {
        logger.LogInformation("({ApiName}) {Title}: {Kind} ({Step})", tracker.Name, outcome.TargetTitle, outcome.Kind, outcome.Step);
        outcomes?.Record(outcome, detail);
    }

    private static string TargetId(EntryState state) => state.Entry.AlternativeId ?? state.Entry.Id.ToString();
}
