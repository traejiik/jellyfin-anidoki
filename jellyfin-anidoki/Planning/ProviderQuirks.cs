using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Planning;

/// <summary>
/// Behaviours of today's sync that the neutral entry state would otherwise hide. Each is kept on purpose and removed only
/// in its own labelled change (pipeline spec, "Quirks preserved deliberately").
/// </summary>
/// <param name="RewatchReadsAsCompleted">
/// MAL reports a rewatch in progress as Completed with a flag, and the sync treats it as Completed: with RewatchCompleted
/// off it is skipped as rewatch-disabled; with it on it goes through the rewatch rules, where it continues or restarts.
/// </param>
/// <param name="MissingEntryReadsAsWatching">
/// Kitsu's client reports a missing library entry as an empty status, so the sync treats it as Watching at 0: it is added
/// even with PlanToWatchOnly on, and completing it sends no start date.
/// </param>
public sealed record ProviderQuirks(bool RewatchReadsAsCompleted, bool MissingEntryReadsAsWatching) {
    public static ProviderQuirks For(ApiName provider) => new(provider == ApiName.Mal, provider == ApiName.Kitsu);
}
