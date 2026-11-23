using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Caching.Memory;

namespace jellyfin_anidoki.Progress;

/// <summary>
/// Each account's score format, read once per link: a new sign-in changes the link's generation and so reads it again.
/// Kept until Jellyfin restarts (no expiry), since an account rarely changes its format.
/// </summary>
public sealed class ScoreFormatCache(IMemoryCache cache) {
    /// <returns>The format, or null when the tracker has no scores or could not say.</returns>
    public async Task<ScoreFormat?> Get(Guid userId, Guid generation, ITrackerProvider tracker, CancellationToken cancellationToken) {
        if (!tracker.Capabilities.SupportsScore)
            return null;
        var key = (nameof(ScoreFormatCache), userId, tracker.Name, generation);
        if (cache.TryGetValue(key, out ScoreFormat cached))
            return cached;
        ScoreFormat? format = await tracker.ReadScoreFormat(cancellationToken);
        if (format is { } known)
            cache.Set(key, known);
        return format;
    }
}
