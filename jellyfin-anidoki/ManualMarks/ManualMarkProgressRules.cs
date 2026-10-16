#nullable enable
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.ManualMarks;

public static class ManualMarkProgressRules {
    public static bool Allows(Anime anime, int? episode, bool statusOnly) {
        var state = anime.MyListStatus;
        if (state?.Status is Status.Completed or Status.Rewatching || state?.IsRewatching == true)
            return false;
        return statusOnly || episode.HasValue && (state == null || episode.Value > state.NumEpisodesWatched);
    }
}
