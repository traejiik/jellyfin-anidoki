using System;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Trackers;

/// <summary>What a provider can do, so callers decide without checking which provider it is.</summary>
public sealed record ProviderCapabilities(bool TracksProgress, bool SupportsRewatch, bool ReportsRewatchCount,
    bool SupportsDates, bool ResolvesByCrossIds) {
    public static ProviderCapabilities For(ApiName provider) => provider switch {
        // MAL reads do not map num_times_rewatched; only its write response reports the count.
        ApiName.Mal => new(true, true, false, true, false),
        ApiName.AniList => new(true, true, true, true, false),
        ApiName.Kitsu => new(true, true, true, true, false),
        // Annict tracks status only and is matched on the MAL ID.
        ApiName.Annict => new(false, false, false, false, true),
        // Shikimori's update call sends no dates; it is matched on the MAL ID when one is known.
        ApiName.Shikimori => new(true, true, true, false, true),
        ApiName.Simkl => new(true, false, false, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
