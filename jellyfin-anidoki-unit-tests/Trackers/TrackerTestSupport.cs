using System;
using System.Collections.Generic;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;

namespace jellyfin_anidoki_unit_tests.Trackers;

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TrackerTestSupport {
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public static readonly ApiName[] AllProviders = [ApiName.Mal, ApiName.AniList, ApiName.Kitsu, ApiName.Annict, ApiName.Shikimori, ApiName.Simkl];

    public static (ApiCallHelpersTrackerProvider Adapter, FakeTracker Tracker) Adapter(ApiName provider) {
        var tracker = new FakeTracker(provider);
        return (new ApiCallHelpersTrackerProvider(provider, tracker, new FixedTime(Now)), tracker);
    }

    /// <summary>A ref the provider can read entry <paramref name="id"/> by, as the resolver would produce it.</summary>
    public static EntryRef Ref(ApiName provider, int id = 500) => provider switch {
        ApiName.Simkl => new EntryRef(provider, id, null, new CrossReference(new ResolvedIds(null, id, null, null), "Fixture Show", MediaKind.Series)),
        ApiName.Annict or ApiName.Shikimori => new EntryRef(provider, id, $"alt-{id}"),
        _ => new EntryRef(provider, id, null)
    };

    public static IEnumerable<ApiName> RewatchProviders() => [ApiName.Mal, ApiName.AniList, ApiName.Kitsu, ApiName.Shikimori];
}
