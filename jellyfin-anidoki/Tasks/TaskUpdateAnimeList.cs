using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
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

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) {
        await Task.Run(async () => {
            await _animeList.Update();
        }, cancellationToken);
    }
}
