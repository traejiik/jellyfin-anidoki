using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Execution;

/// <summary>One target and how it was found, carried so every record of it can say so.</summary>
public sealed record ActionTrace(ResolvedTarget Target, ResolvedBy By, string? Note);

/// <summary>What planning one provider's resolution produced, in the order <see cref="Executor.Execute"/> acts on it.</summary>
public abstract record PlanStep;

/// <summary>A target that was read and planned. Nothing has been written or recorded for it.</summary>
/// <param name="Read">The entry as read, before provider quirks; a later re-read is compared with it.</param>
public sealed record PlannedTarget(ActionTrace Trace, EntryState Read, PlanDecision Decision) : PlanStep;

/// <summary>An outcome settled without a write: unresolved, ignored, unreadable, or a failed read.</summary>
/// <param name="StopsProvider">True when nothing else should be asked of this provider for this source.</param>
public sealed record PlannedOutcome(ProviderWriteOutcome Outcome, OutcomeDetail? Detail, bool StopsProvider) : PlanStep;

/// <summary>How <see cref="Executor.ApplyPlanned"/> checks a planned write before sending it.</summary>
public enum ApplyMode {
    /// <summary>Re-read only a read older than <see cref="Executor.StaleAfter"/>, and re-plan if it moved. Automatic sync.</summary>
    Automatic,

    /// <summary>Always re-read before writing, and refuse with <c>Skipped(preview-stale)</c> if the entry moved; never re-plan.
    /// Selected preview rows, which must be written as the admin saw them or not at all.</summary>
    Strict
}
