namespace jellyfin_anidoki.Playback;

/// <summary>
/// D3: the completion percentage, from 60% up to the server's own (<c>MaxResumePct</c>) in steps of 5. Reached during
/// playback, it updates the trackers there and then; Jellyfin's own watched mark is untouched. No percentage updates
/// them when the player exits, at Jellyfin's own percentage.
/// The admin sets a default (<c>DefaultTrackerThresholdPercent</c>, null for player exit). A user follows it while
/// their own choice is null, or picks a percentage, or <see cref="PlayerExit"/> to keep player exit whatever the default.
/// </summary>
public static class TrackerThreshold {
    public const int Minimum = 60;
    public const int Step = 5;

    /// <summary>A user's choice of updating on player exit, even when the admin's default is a percentage.</summary>
    public const int PlayerExit = 0;

    /// <summary>True for no percentage (the default, on player exit) or one the server's percentage allows.</summary>
    public static bool IsValid(int? chosen, int maxResumePct) =>
        chosen is not int percent || percent >= Minimum && percent <= maxResumePct && percent % Step == 0;

    /// <summary>True for a user's choice: following the default, player exit, or a valid percentage.</summary>
    public static bool IsValidForUser(int? chosen, int maxResumePct) => chosen == PlayerExit || IsValid(chosen, maxResumePct);

    /// <summary>The percentage that applies, or null for the default: also when the server's percentage dropped below the choice.</summary>
    public static int? Effective(int? chosen, int maxResumePct) => chosen is int percent && IsValid(percent, maxResumePct) ? percent : null;

    /// <summary>
    /// The percentage that applies to a user, or null for player exit. A choice above the server's percentage falls back
    /// to the admin's default, as does no choice.
    /// </summary>
    public static int? ForUser(int? userChoice, int? serverDefault, int maxResumePct) => userChoice switch {
        PlayerExit => null,
        int percent when IsValid(percent, maxResumePct) => percent,
        _ => Effective(serverDefault, maxResumePct)
    };
}
