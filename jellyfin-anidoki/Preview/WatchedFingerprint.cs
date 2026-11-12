using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace jellyfin_anidoki.Preview;

/// <summary>D4: what Jellyfin had played of a season when it was previewed, so an apply can tell if that moved.</summary>
internal static class WatchedFingerprint {
    /// <summary>The played episode numbers, sorted, with runs collapsed: <c>1-12,14</c>. Empty when nothing is played.</summary>
    public static string Of(IEnumerable<int> episodes) {
        var parts = new List<string>();
        int? start = null;
        int end = 0;
        foreach (int episode in episodes.Distinct().Order()) {
            if (start != null && episode == end + 1) {
                end = episode;
                continue;
            }
            if (start != null)
                parts.Add(Part(start.Value, end));
            start = episode;
            end = episode;
        }
        if (start != null)
            parts.Add(Part(start.Value, end));
        return string.Join(',', parts);
    }

    private static string Part(int start, int end) =>
        start == end ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{end}");
}
