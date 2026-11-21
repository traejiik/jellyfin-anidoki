using System;

namespace jellyfin_anidoki.Playback;

/// <summary>One playback report: where the player was, whether it was paused, and when AniDōki received it.</summary>
public sealed record WatchReport(long PositionTicks, bool IsPaused, DateTimeOffset At);

/// <summary>The item's runtime and the server's resume settings, read when a watch is judged.</summary>
public sealed record WatchRules(long? RuntimeTicks, int MaxResumePct, int MinResumeDurationSeconds);

/// <summary>
/// What is known about one watch so far. <see cref="StartTicks"/> is where it began (0 unless it resumed a saved
/// position); <see cref="PlayedTicks"/> and <see cref="SkippedTicks"/> split every forward step into time that
/// could have been played and time that was jumped over. <see cref="SlackTicks"/> is playable time not yet used
/// (at most <see cref="WatchEvaluator.MaxSlack"/>), so a client report that catches up with Jellyfin's own estimate is not lost.
/// </summary>
public sealed record WatchState(long StartTicks, long PlayedTicks, long SkippedTicks, long LastTicks, bool LastPaused,
    DateTimeOffset LastAt, int ProgressReports, bool ThresholdCrossed, long SlackTicks = 0);

/// <summary>Which rule decided a stop.</summary>
public enum WatchRule {
    /// <summary>Jellyfin did not report the item as played to completion.</summary>
    NotPlayedToCompletion,
    /// <summary>The item has no runtime, so only Jellyfin's completion can apply.</summary>
    JellyfinUnknownRuntime,
    /// <summary>The item is shorter than <c>MinResumeDurationSeconds</c>; Jellyfin gives it no percentage.</summary>
    JellyfinShortItem,
    /// <summary>The client sent too few reports and no stop position, so nothing could be measured.</summary>
    JellyfinNoPosition,
    /// <summary>The client sent fewer than 2 progress reports; the stop position stands in for the measurement.</summary>
    StopPosition,
    /// <summary>The credited position was measured from the reports.</summary>
    Measured
}

/// <summary>Whether a stop counts as a watch, with the numbers that decided it.</summary>
public sealed record WatchVerdict(bool Counts, WatchRule Rule, long CreditedTicks, long RequiredTicks, int ProgressReports);

/// <summary>
/// Measures a watch from Jellyfin's playback reports (Phase 5 D2, D3). Pure: every function takes the state and a
/// report and returns the new state; the time of a report arrives in the report, so tests control the clock.
/// </summary>
public static class WatchEvaluator {
    /// <summary>
    /// Playback counts as played up to this many times the wall-clock time between reports. Reports carry no
    /// playback speed, so 2.5 lets 2× playback through while a seek, which jumps minutes, is still caught.
    /// </summary>
    public const double StepCap = 2.5;

    /// <summary>
    /// How much playable time not used by one step carries over to the next. Jellyfin 12.4 reports every second
    /// itself, estimating the position at normal speed from the client's last report, so at 2× the client's next
    /// report jumps ahead in almost no time; the carried time covers that jump, and a seek is far longer.
    /// </summary>
    public static readonly TimeSpan MaxSlack = TimeSpan.FromSeconds(30);

    /// <summary>A first report this close to the saved resume position is a resume, and the time before it counts.</summary>
    public static readonly TimeSpan ResumeTolerance = TimeSpan.FromSeconds(30);

    /// <summary>A report this soon after the last one and not ahead of it arrived out of order, or twice.</summary>
    public static readonly TimeSpan OutOfOrderWindow = TimeSpan.FromSeconds(1);

    private static readonly long MinimumAllowance = TimeSpan.FromMinutes(3).Ticks;

    /// <summary>Starts a watch from its first report.</summary>
    /// <param name="savedTicks">The position Jellyfin had saved for this user and item before the watch.</param>
    /// <param name="isProgress">True when the first report seen is a progress report, not the start.</param>
    public static WatchState Begin(WatchReport first, long? savedTicks, bool isProgress = false) {
        long position = Math.Max(0, first.PositionTicks);
        bool resumed = Math.Abs(position - (savedTicks ?? 0)) <= ResumeTolerance.Ticks;
        return new WatchState(resumed ? position : 0, 0, resumed ? 0 : position, position, first.IsPaused, first.At,
            isProgress ? 1 : 0, false);
    }

    /// <summary>Adds a progress report.</summary>
    /// <param name="thresholdPercent">An earlier threshold to watch for; null when none applies.</param>
    /// <returns>The new state, and true the one time the credited position reaches <paramref name="thresholdPercent"/>.</returns>
    public static (WatchState State, bool Crossed) Report(WatchState state, WatchReport report, long? runtimeTicks,
        int? thresholdPercent = null) {
        WatchState next = Step(state, report);
        if (ReferenceEquals(next, state))
            return (state, false);
        next = next with { ProgressReports = state.ProgressReports + 1 };
        if (next.ThresholdCrossed || thresholdPercent is not int percent || runtimeTicks is not > 0)
            return (next, false);
        if (Credited(next, runtimeTicks.Value) < Required(runtimeTicks.Value, percent))
            return (next, false);
        return (next with { ThresholdCrossed = true }, true);
    }

    /// <summary>Skipped time up to this much is forgiven: max(10% of the runtime, 3 minutes).</summary>
    public static long Allowance(long runtimeTicks) => Math.Max(runtimeTicks / 10, MinimumAllowance);

    /// <summary>How far into the item the user is treated as having watched, never past its end.</summary>
    public static long Credited(WatchState state, long runtimeTicks) =>
        Math.Min(runtimeTicks, state.StartTicks + state.PlayedTicks + Math.Min(state.SkippedTicks, Allowance(runtimeTicks)));

    /// <summary>Judges a stop (D3): Jellyfin's completion, and, where a percentage can apply, a credited position past it.</summary>
    /// <param name="state">The watch, or null when no report was seen before the stop.</param>
    public static WatchVerdict Stop(WatchState? state, long? stopTicks, DateTimeOffset at, bool playedToCompletion, WatchRules rules) {
        int reports = state?.ProgressReports ?? 0;
        if (!playedToCompletion)
            return new WatchVerdict(false, WatchRule.NotPlayedToCompletion, 0, 0, reports);
        if (rules.RuntimeTicks is not long runtime || runtime <= 0)
            return new WatchVerdict(true, WatchRule.JellyfinUnknownRuntime, 0, 0, reports);
        if (runtime < TimeSpan.FromSeconds(rules.MinResumeDurationSeconds).Ticks)
            return new WatchVerdict(true, WatchRule.JellyfinShortItem, 0, 0, reports);
        long required = Required(runtime, rules.MaxResumePct);
        if (reports < 2) {
            if (stopTicks is not long stop)
                return new WatchVerdict(true, WatchRule.JellyfinNoPosition, 0, required, reports);
            long fromStop = Math.Clamp(stop, 0, runtime);
            return new WatchVerdict(fromStop >= required, WatchRule.StopPosition, fromStop, required, reports);
        }
        WatchState last = state!;
        if (stopTicks is long final)
            last = Step(last, new WatchReport(final, last.LastPaused, at));
        long credited = Credited(last, runtime);
        return new WatchVerdict(credited >= required, WatchRule.Measured, credited, required, reports);
    }

    private static long Required(long runtimeTicks, int percent) => (long)(runtimeTicks * (percent / 100.0));

    /// <summary>Moves the watch to a new position. Returns <paramref name="state"/> itself when the report is ignored.</summary>
    private static WatchState Step(WatchState state, WatchReport report) {
        long position = Math.Max(0, report.PositionTicks);
        long step = position - state.LastTicks;
        TimeSpan wall = report.At - state.LastAt;
        if (step < 0 && wall < OutOfOrderWindow)
            return state;
        var moved = state with { LastTicks = position, LastPaused = report.IsPaused, LastAt = report.At };
        if (state.LastPaused)
            return step > 0 ? moved with { SkippedTicks = state.SkippedTicks + step } : moved;
        long playable = state.SlackTicks + (long)(Math.Max(0, wall.Ticks) * StepCap);
        long played = Math.Clamp(step, 0, playable);
        return moved with {
            PlayedTicks = state.PlayedTicks + played,
            SkippedTicks = state.SkippedTicks + Math.Max(0, step - played),
            SlackTicks = Math.Min(MaxSlack.Ticks, playable - played)
        };
    }
}
