using System;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Planning;

/// <summary>
/// Decides what to write for one target. Pure: no I/O, no clock, no provider names (quirks arrive as flags).
/// Moved from UpdateProviderStatus.CheckUserListAnimeStatusBase, CheckIfRewatchCompleted, UpdateAnimeStatus and
/// UpdateAnnictStatus, keeping their order where it differs from the pipeline spec's tables.
/// </summary>
public static class Planner {
    public static PlanDecision Plan(PlanInput input) {
        EntryState state = input.Quirks.MissingEntryReadsAsWatching && input.Entry.List == null
            ? input.Entry with { List = new ListState(TrackerStatus.Watching, 0, false, null) }
            : input.Entry;
        PlanDecision decision = input.Capabilities.TracksProgress ? PlanProgress(input, state) : PlanStatusOnly(input, state);
        // manual marks never start or finish a rewatch; P1 already prevents it, this keeps it true if the rules move
        if (input.Policy == PlanPolicy.ConfirmedManual && decision is Write { Change.Rewatch: not RewatchTransition.None })
            return new NoChange("manual-forward-only");
        return decision;
    }

    private static PlanDecision PlanProgress(PlanInput input, EntryState state) {
        int episode = input.Episode;
        ListState? list = state.List;
        bool completed = list is { Status: TrackerStatus.Completed };
        if (input.Policy == PlanPolicy.ConfirmedManual && list != null && (completed || list.Rewatching || episode <= list.Progress))
            return new NoChange("manual-forward-only");
        if (list is { Status: TrackerStatus.Watching } || (list is { Rewatching: true } && !input.Quirks.RewatchReadsAsCompleted))
            return Advance(input, state);
        if (input.Rules.PlanToWatchOnly) {
            // plan to watch is checked before progress here, unlike the spec's table
            if (list is { Status: TrackerStatus.PlanToWatch })
                return Advance(input, state);
            if (completed)
                return Rewatch(input, state);
            if (list != null && list.Progress >= episode)
                return new NoChange("already-watched");
            return new Skip("plan-to-watch-only");
        }

        if (completed)
            return Rewatch(input, state);
        if (list != null && list.Progress >= episode)
            return new NoChange("already-watched");
        return Advance(input, state);
    }

    /// <summary>A completed entry played again: P3–P5, with today's branch for an entry behind the episode.</summary>
    private static PlanDecision Rewatch(PlanInput input, EntryState state) {
        if (!input.Capabilities.SupportsRewatch)
            return new Skip("rewatch-unsupported");
        if (!input.Rules.RewatchCompleted)
            return new Skip("rewatch-disabled");
        ListState list = state.List!;
        int episode = input.Episode;
        int total = state.TotalEpisodes;
        if (list.Progress > episode && total != 1)
            return Changed(input, state, new EntryChange(episode, TrackerStatus.Completed, RewatchTransition.Start));
        if (episode == total || total == 1)
            return Changed(input, state, new EntryChange(total == 1 ? 1 : episode, TrackerStatus.Completed, RewatchTransition.Finish));
        // only a MAL rewatch reaches here still rewatching (RewatchReadsAsCompleted); it continues
        if (list.Rewatching)
            return Changed(input, state, new EntryChange(episode, TrackerStatus.Completed, RewatchTransition.Continue));
        // a completed entry behind the played episode goes back to Watching without a rewatch, as today
        return Changed(input, state, new EntryChange(episode, TrackerStatus.Watching, StartDate: episode == 1 ? input.Now : null));
    }

    private static PlanDecision Advance(PlanInput input, EntryState state) {
        int episode = input.Episode;
        int total = state.TotalEpisodes;
        DateTime? started = episode == 1 ? input.Now : null;
        if (state.List is not { } list) {
            return episode == total
                ? Changed(input, state, new EntryChange(episode, TrackerStatus.Completed, StartDate: input.Now, EndDate: input.Now))
                : Changed(input, state, new EntryChange(episode, TrackerStatus.Watching, StartDate: started));
        }

        // a single-episode entry is written as completed again even at progress 1 (spec quirk)
        if (list.Progress >= episode && total != 1)
            return new NoChange("already-watched");
        if (episode == total || total == 1) {
            int progress = total == 1 ? 1 : episode;
            if (list.Rewatching)
                return Changed(input, state, new EntryChange(progress, TrackerStatus.Completed, RewatchTransition.Finish));
            // a movie or OVA starts and ends on the same day
            return Changed(input, state, new EntryChange(progress, TrackerStatus.Completed,
                StartDate: total == 1 ? input.Now : null, EndDate: input.Now));
        }

        return list.Rewatching
            ? Changed(input, state, new EntryChange(episode, TrackerStatus.Completed, RewatchTransition.Continue, StartDate: started))
            : Changed(input, state, new EntryChange(episode, TrackerStatus.Watching, StartDate: started));
    }

    private static PlanDecision PlanStatusOnly(PlanInput input, EntryState state) {
        ListState? list = state.List;
        bool completed = list is { Status: TrackerStatus.Completed };
        if (input.Policy == PlanPolicy.ConfirmedManual && (completed || list is { Rewatching: true }))
            return new NoChange("manual-forward-only");
        if (input.Rules.PlanToWatchOnly)
            return list is { Status: TrackerStatus.PlanToWatch } ? StatusUpdate(input, state) : new Skip("plan-to-watch-only");
        // rewatch-disabled where rewatch-unsupported would be accurate (spec quirk)
        if (completed)
            return new Skip("rewatch-disabled");
        return StatusUpdate(input, state);
    }

    private static PlanDecision StatusUpdate(PlanInput input, EntryState state) {
        int episode = input.Episode;
        int total = state.TotalEpisodes;
        if (episode == total)
            return Changed(input, state, new EntryChange(episode, TrackerStatus.Completed));
        // never Watching while the total is unknown (spec quirk)
        if (total > episode && state.List?.Status != TrackerStatus.Watching)
            return Changed(input, state, new EntryChange(episode, TrackerStatus.Watching));
        return new NoChange("status");
    }

    private static Write Changed(PlanInput input, EntryState state, EntryChange change) =>
        new(change, change.Rewatch == RewatchTransition.Finish ? "rewatch-count" : input.Capabilities.TracksProgress ? "progress" : "status", state);
}
