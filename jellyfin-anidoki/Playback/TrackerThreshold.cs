namespace jellyfin_anidoki.Playback;

/// <summary>
/// D3: a user's optional completion percentage, from 60% up to the server's own (<c>MaxResumePct</c>) in steps of 5.
/// Reached during playback, it updates the trackers there and then; Jellyfin's own watched mark is untouched.
/// </summary>
public static class TrackerThreshold {
    public const int Minimum = 60;
    public const int Step = 5;

    /// <summary>True for no percentage (the default, on player exit) or one the server's percentage allows.</summary>
    public static bool IsValid(int? chosen, int maxResumePct) =>
        chosen is not int percent || percent >= Minimum && percent <= maxResumePct && percent % Step == 0;

    /// <summary>The percentage that applies, or null for the default: also when the server's percentage dropped below the choice.</summary>
    public static int? Effective(int? chosen, int maxResumePct) => chosen is int percent && IsValid(percent, maxResumePct) ? percent : null;
}
