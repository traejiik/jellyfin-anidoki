using System;
using System.Collections.Generic;
using System.Net.Http;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Api.Anilist;
using jellyfin_anidoki.Api.Annict;
using jellyfin_anidoki.Api.Kitsu;
using jellyfin_anidoki.Api.Shikimori;
using jellyfin_anidoki.Api.Simkl;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Trackers;

/// <summary>
/// Builds one provider's client for a user. Shikimori and Simkl need server configuration first; without it there is
/// no client, and the caller moves to the next provider.
/// </summary>
internal sealed class TrackerClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory,
    IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache,
    IAsyncDelayer delayer, ILogger logger) {
    /// <summary>Tests set this to substitute an in-memory tracker; production leaves it null.</summary>
    internal Func<ApiName, UserConfig, IApiCallHelpers>? Override { get; set; }

    /// <returns>The client, or null when the provider is not configured on this server.</returns>
    public IApiCallHelpers? Create(ApiName provider, UserConfig user) {
        switch (provider) {
            case ApiName.Mal:
                return Build(provider, user, () => new ApiCallHelpers(malApiCalls: new MalApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, user)));
            case ApiName.AniList:
                return Build(provider, user, () => new ApiCallHelpers(aniListApiCalls: new AniListApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, user)));
            case ApiName.Kitsu:
                return Build(provider, user, () => new ApiCallHelpers(kitsuApiCalls: new KitsuApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, user)));
            case ApiName.Annict:
                return Build(provider, user, () => new ApiCallHelpers(annictApiCalls: new AnnictApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, user)));
            case ApiName.Shikimori:
                string? shikimoriAppName = ConfigHelper.GetShikimoriAppName(logger);
                if (string.IsNullOrEmpty(shikimoriAppName))
                    return null;
                return Build(provider, user, () => new ApiCallHelpers(shikimoriApiCalls: new ShikimoriApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, new Dictionary<string, string> { { "User-Agent", shikimoriAppName } }, user)));
            case ApiName.Simkl:
                string? simklClientId = ConfigHelper.GetSimklClientId(logger);
                if (string.IsNullOrEmpty(simklClientId))
                    return null;
                return Build(provider, user, () => new ApiCallHelpers(simklApiCalls: new SimklApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, delayer, new Dictionary<string, string> { { "simkl-api-key", simklClientId } }, user)));
            default:
                return null;
        }
    }

    public ITrackerProvider Adapt(ApiName provider, IApiCallHelpers client) =>
        new ApiCallHelpersTrackerProvider(provider, client, TimeProvider.System);

    private IApiCallHelpers Build(ApiName provider, UserConfig user, Func<IApiCallHelpers> createProductionClient) =>
        Override?.Invoke(provider, user) ?? createProductionClient();
}
