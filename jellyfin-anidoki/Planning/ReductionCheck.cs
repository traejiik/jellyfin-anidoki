using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Planning;

/// <summary>What a preview offers when the tracker is ahead of Jellyfin.</summary>
public abstract record ReductionAnswer;

/// <summary>Lower the entry's progress to Jellyfin's and keep its status. Offered, never selected by default.</summary>
public sealed record LowerProgress(EntryChange Change) : ReductionAnswer;

/// <summary>The tracker is ahead on purpose; the row stays NoChange with this reason.</summary>
public sealed record KeepTrackerProgress(string Reason) : ReductionAnswer;

/// <summary>
/// D3: whether a preview offers to lower an entry's progress to Jellyfin's. Pure. Only the preview calls it, after the
/// planner chose not to write; automatic sync and Run sync never lower progress.
/// </summary>
public static class ReductionCheck {
    public const string Reason = "reduction";
    public const string CompletedOnTracker = "completed-on-tracker";
    public const string DroppedOnTracker = "dropped-on-tracker";

    /// <param name="read">The entry as read, before provider quirks.</param>
    /// <param name="episode">Jellyfin's progress for this entry.</param>
    /// <returns>Null when the tracker is not ahead, or when there is nothing to offer or say.</returns>
    public static ReductionAnswer? Check(EntryState read, int episode, ProviderCapabilities capabilities) {
        if (!capabilities.TracksProgress || read.List is not { } list || list.Progress <= episode || list.Rewatching)
            return null;
        return list.Status switch {
            TrackerStatus.Watching or TrackerStatus.OnHold => new LowerProgress(new EntryChange(episode, list.Status)),
            TrackerStatus.Completed => new KeepTrackerProgress(CompletedOnTracker),
            TrackerStatus.Dropped => new KeepTrackerProgress(DroppedOnTracker),
            _ => null
        };
    }
}
