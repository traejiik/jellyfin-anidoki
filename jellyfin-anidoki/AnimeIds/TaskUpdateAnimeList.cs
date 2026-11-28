using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Model.Tasks;

namespace jellyfin_anidoki;

public class TaskUpdateAnimeList : IScheduledTask {
    private readonly AnimeListSource _animeList;

    public string Name => "AniDoki Update Anime List";
    public string Key => "UpdateAnimeList";
    public string Description => "Update the anime list to the latest version.";
    public string Category => "AniDoki";

    public TaskUpdateAnimeList(AnimeListSource animeList) {
        _animeList = animeList;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
        var trigger = new TaskTriggerInfo {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        };

        return new[] { trigger };
    }

    /// <summary>Throws when the update failed, so Jellyfin shows the run as Failed rather than Completed.</summary>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) {
        if (!await _animeList.Update())
            throw new InvalidOperationException("Could not update the anime list; see the AniDoki log");
    }
}
