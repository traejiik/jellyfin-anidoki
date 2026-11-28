using System;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

/// <summary>
/// D5: Sync to a tracker for one series: Phase 4's manual sync, given only that series' latest watched episode per season
/// and only that tracker. The planner still never lowers progress.
/// </summary>
public sealed class SeriesOutbound(IUserManager userManager, ILibraryManager libraryManager, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, ArmClient arm, AnimeListSource animeList, IMemoryCache memoryCache,
    ISeasonWatchState watchState) {
    private readonly ILogger<SeriesOutbound> _logger = loggerFactory.CreateLogger<SeriesOutbound>();

    internal async Task Run(Guid userId, Guid seriesId, ApiName provider, OperationContext operation) {
        if (libraryManager.GetItemById(seriesId) is not Series series || LatestWatchedEpisodes.OfSeries(series, userId, userManager, _logger) is not { } targets) {
            operation.Result = OperationState.Skipped;
            operation.Message = "The series or the user is no longer in Jellyfin.";
            return;
        }
        var sync = new SyncProviderFromLocal(userManager, libraryManager, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor,
            arm, animeList, memoryCache, new Delayer(), userId.ToString(), watchState) { OnlyProvider = provider };
        await sync.SyncEpisodes(targets, operation);
    }
}
