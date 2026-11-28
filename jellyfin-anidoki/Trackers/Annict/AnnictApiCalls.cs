using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Trackers.Annict;

/// <remarks>
/// A 200 reply without its data still throws where it is read, as it always has: the pipeline then treats the read as
/// failed. Returning null there would read as "not on the list" and could lead to a write.
/// </remarks>
public class AnnictApiCalls {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AnnictApiCalls> _logger;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMemoryCache _memoryCache;
    private readonly IAsyncDelayer _delayer;
    private readonly UserConfig? _userConfig;
    public static readonly int PageSize = 1000;


    public AnnictApiCalls(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache, IAsyncDelayer delayer, UserConfig? userConfig = null) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AnnictApiCalls>();
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _userConfig = userConfig;
        _memoryCache = memoryCache;
        _delayer = delayer;
    }

    /// <summary>
    /// Search for an anime based upon its name.
    /// </summary>
    /// <param name="searchString">The name to search for.</param>
    /// <returns>List of anime.</returns>
    public async Task<List<AnnictSearch.AnnictAnime>?> SearchAnime(string searchString) {
        string query = @"query ($title: String!) {
            searchWorks(titles: [$title]) {
                nodes {
                    id
                    titleEn
                    malAnimeId
                    viewerStatusState
                    episodesCount
                }
            }
        }
        ";

        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "title", searchString }
        };

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, variables);
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var result = JsonSerializer.Deserialize<AnnictSearch.AnnictSearchMedia>(await streamReader.ReadToEndAsync());

            return result!.AnnictSearchData!.SearchWorks!.Nodes;
        }

        return null;
    }

    /// <summary>
    /// Update an anime status.
    /// </summary>
    /// <param name="id">ID of the anime in the provider.</param>
    /// <param name="status">Status to set the anime as.</param>
    /// <returns>True if the anime has been updated.</returns>
    public async Task<bool> UpdateAnime(string id, AnnictSearch.AnnictMediaStatus status) {
        string query = @"mutation ($workId: ID!, $state: StatusState!" +
                       @") {
          updateStatus (input: {workId:$workId, state:$state}" +
                       @") {
            clientMutationId
          }
        }";

        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "workId", id },
            { "state", status.ToString().ToUpper() },
        };

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, variables);
        return await MutationAcknowledgement.Validate(response, root =>
            MutationAcknowledgement.Object(root, "data", out var data) &&
            MutationAcknowledgement.Object(data, "updateStatus", out var entry) &&
            entry.TryGetProperty("clientMutationId", out var clientMutationId) && (clientMutationId.ValueKind == JsonValueKind.Null || clientMutationId.ValueKind == JsonValueKind.String));
    }

    /// <summary>
    /// Get the users anime list.
    /// </summary>
    /// <param name="status">Status to filter by.</param>
    /// <returns>List of anime in the users list.</returns>
    public async Task<List<AnnictSearch.AnnictAnime>?> GetAnimeList(AnnictSearch.AnnictMediaStatus status) {
        string query = @"query($state: [StatusState!], $perPage: Int, $after: String!) {
            viewer {
                libraryEntries(states:$state, first: $perPage,after: $after) {
                    nodes {
                        work {
                            id
                            titleEn
                            malAnimeId
                            viewerStatusState
                            episodesCount
                        },
                    },
                    pageInfo {
                      hasNextPage,
                      endCursor
                    }
                }
            }
        }
        ";

        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "state", status.ToString().ToUpper() },
            { "perPage", PageSize },
            { "after", string.Empty }
        };

        AnnictMediaList.AnnictUserMediaList? result = null;

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, variables);
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            result = JsonSerializer.Deserialize<AnnictMediaList.AnnictUserMediaList>(await streamReader.ReadToEndAsync());
        }
        if (result != null) {
            AnnictMediaList.AnnictUserMediaListLibraryEntries entries = result.AnnictSearchData!.Viewer!.AnnictUserMediaListLibraryEntries!;
            while (entries.PageInfo!.HasNextPage) {
                string? cursor = entries.PageInfo.EndCursor;
                variables["after"] = cursor ?? string.Empty;
                var paginatedResponse = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, variables);
                // A page that failed fails the whole list, as a failed first page does.
                if (paginatedResponse == null)
                    return null;
                StreamReader streamReader = new StreamReader(await paginatedResponse.Content.ReadAsStreamAsync());
                var paginatedResult = JsonSerializer.Deserialize<AnnictMediaList.AnnictUserMediaList>(await streamReader.ReadToEndAsync());
                AnnictMediaList.AnnictUserMediaListLibraryEntries next = paginatedResult!.AnnictSearchData!.Viewer!.AnnictUserMediaListLibraryEntries!;
                // A page that says there is more without moving on would be asked for forever.
                if (next.PageInfo!.HasNextPage && next.PageInfo.EndCursor == cursor) {
                    _logger.LogWarning("({Provider}) Could not read the next page of the list; the page cursor did not move", ApiName.Annict);
                    return null;
                }

                entries.Nodes = entries.Nodes!.Concat(next.Nodes!).ToList();
                entries.PageInfo = next.PageInfo;
                if (!next.PageInfo!.HasNextPage) {
                    break;
                }


                // sleeping thread so we dont hammer the API
                await _delayer.Delay(TimeSpan.FromSeconds(1));
            }

            return entries.Nodes;
        }

        return null;
    }

    /// <summary>
    /// Get a singular anime.
    /// </summary>
    /// <param name="id">ID of the anime you want to get.</param>
    /// <returns>The retrieved anime.</returns>
    public async Task<AnnictSearch.AnnictAnime?> GetAnime(string id) {
        string query = @"query ($id: ID!) {
          node(id: $id) {
            ... on Work {
                id
                titleEn
                malAnimeId
                viewerStatusState
                episodesCount
            }
          }
        }";


        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "id", id }
        };

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, variables);
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var result = JsonSerializer.Deserialize<AnnictGetMedia.AnnictGetMediaRoot>(await streamReader.ReadToEndAsync());

            if (result != null) {
                return result.AnnictGetMediaData!.Node;
            }
        }

        return null;
    }

    public async Task<AnnictViewer.AnnictViewerRoot?> GetCurrentUser() {
        string query = @"query {
          viewer {
            username
          }
        }";

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.Annict, new Dictionary<string, object>());
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var result = JsonSerializer.Deserialize<AnnictViewer.AnnictViewerRoot>(await streamReader.ReadToEndAsync());

            if (result != null) {
                return result;
            }
        }

        return null;
    }
}
