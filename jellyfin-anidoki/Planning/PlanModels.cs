using System;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Planning;

/// <summary>A user's sync rules, read once per sync.</summary>
public sealed record PlanRules(bool PlanToWatchOnly, bool RewatchCompleted);

/// <summary>ConfirmedManual only moves progress forward and never starts or finishes a rewatch; every other trigger is Automatic.</summary>
public enum PlanPolicy { Automatic, ConfirmedManual }

/// <param name="Episode">The target episode on this entry (for an earlier part, its length).</param>
/// <param name="Now">Local time, used for start and end dates.</param>
public sealed record PlanInput(int Episode, EntryState Entry, PlanRules Rules, PlanPolicy Policy,
    ProviderCapabilities Capabilities, ProviderQuirks Quirks, DateTime Now);

/// <param name="Reason">The step recorded with the outcome; existing step strings, so notifications are unchanged.</param>
public abstract record PlanDecision(string Reason);

/// <param name="PlannedAgainst">The read state after quirks; <see cref="ITrackerProvider.Apply"/> and the outcome compare with it.</param>
public sealed record Write(EntryChange Change, string Reason, EntryState PlannedAgainst) : PlanDecision(Reason);

public sealed record NoChange(string Reason) : PlanDecision(Reason);

public sealed record Skip(string Reason) : PlanDecision(Reason);
