using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Api.Anilist;

/// <remarks>
/// A 200 reply without its data still throws where it is read, as it always has: the pipeline then treats the read as
/// failed. Returning null there would read as "not on the list" and could lead to a write.
/// </remarks>
public class AniListApiCalls {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly IAsyncDelayer _delayer;
    private readonly UserConfig? _userConfig;
    private readonly ILogger<AniListApiCalls> _logger;
    private static readonly int PageSize = 50;
    private static readonly int ChunkSize = 500;


    public AniListApiCalls(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache, IAsyncDelayer delayer, UserConfig? userConfig = null) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _httpClient = httpClientFactory.CreateClient(NamedClient.Default);
        _memoryCache = memoryCache;
        _delayer = delayer;
        _userConfig = userConfig;
        _logger = loggerFactory.CreateLogger<AniListApiCalls>();
    }

    /// <summary>
    /// Search for an anime based upon its name.
    /// </summary>
    /// <param name="searchString">The name to search for.</param>
    /// <returns>List of anime.</returns>
    public async Task<List<AniListSearch.Media>?> SearchAnime(string searchString) {
        string query = @"query ($search: String!, $type: MediaType, $perPage: Int, $page: Int) {
            Page(perPage: $perPage, page: $page) {
                pageInfo {
                    total
                        perPage
                    currentPage
                        lastPage
                    hasNextPage
                }
                media(search: $search, type: $type) {
                    id
                    title {
                        romaji
                            english
                        native
                            userPreferred
                    },
                    synonyms
                    episodes
                    status
                    isAdult
                }
            }
        }
        ";

        int page = 1;
        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "search", searchString },
            { "type", "ANIME" },
            { "perPage", PageSize.ToString() },
            { "page", page.ToString() }
        };

        AniListSearch.AniListSearchMedia? result = await GraphQlHelper.DeserializeRequest<AniListSearch.AniListSearchMedia>(_httpClient, _logger, ApiName.AniList, _memoryCache, _delayer, query, variables);

        if (result != null) {
            AniListSearch.Page found = result.Data!.Page!;
            if (found.PageInfo!.HasNextPage) {
                // impose a hard limit of 10 pages
                while (page < 10) {
                    page++;
                    variables["page"] = page.ToString();

                    AniListSearch.AniListSearchMedia? nextPageResult = await GraphQlHelper.DeserializeRequest<AniListSearch.AniListSearchMedia>(_httpClient, _logger, ApiName.AniList, _memoryCache, _delayer, query, variables);
                    // A page that failed fails the whole search, as a failed first page does.
                    if (nextPageResult == null)
                        return null;

                    AniListSearch.Page next = nextPageResult.Data!.Page!;
                    found.Media = found.Media!.Concat(next.Media!).ToList();
                    if (!next.PageInfo!.HasNextPage) {
                        break;
                    }

                    // sleeping thread so we dont hammer the API
                    await _delayer.Delay(TimeSpan.FromSeconds(1));
                }
            }

            return found.Media;
        }

        return null;
    }

    /// <summary>
    /// Get a singular anime.
    /// </summary>
    /// <param name="id">ID of the anime you want to get.</param>
    /// <returns>The retrieved anime.</returns>
    public async Task<AniListSearch.Media?> GetAnime(int id) {
        string query = @"query ($id: Int) {
          Media(id: $id) {
            id
            episodes
            isAdult
            title {
              romaji
              english
              native
              userPreferred
            }
            relations {
              edges {
                relationType
                node {
                  id
                  type
                  coverImage { medium }
                  title {
                    romaji
                    english
                    native
                    userPreferred
                  }
                }
              }
            }
            mediaListEntry {
              status
              progress
              repeat
              score(format: POINT_100)
              startedAt {
                day
                month
                year
              }
              completedAt {
                day
                month
                year
              }
            }
          }
        }";


        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "id", id.ToString() }
        };

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.AniList, variables);
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var result = JsonSerializer.Deserialize<AniListGet.AniListGetMedia>(await streamReader.ReadToEndAsync());

            if (result != null) {
                return result.Data!.Media;
            }
        }

        return null;
    }

    public async Task<AniListViewer.Viewer?> GetCurrentUser() {
        string query = @"query {
          Viewer {
            id
            name
          }
        }";

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.AniList);
        if (response != null) {
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var result = JsonSerializer.Deserialize<AniListViewer.AniListGetViewer>(await streamReader.ReadToEndAsync());

            if (result != null) {
                return result.Data!.Viewer;
            }
        }

        return null;
    }

    /// <returns>The signed-in account's scoring format, or null when it cannot be read or is not one AniDōki knows.</returns>
    public async Task<ScoreFormat?> GetScoreFormat() {
        string query = @"query {
          Viewer {
            id
            mediaListOptions {
              scoreFormat
            }
          }
        }";

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.AniList);
        if (response == null)
            return null;
        using var streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var result = JsonSerializer.Deserialize<AniListViewer.AniListGetViewer>(await streamReader.ReadToEndAsync());
        return result?.Data?.Viewer?.MediaListOptions?.ScoreFormat switch {
            "POINT_100" => ScoreFormat.Point100,
            "POINT_10_DECIMAL" => ScoreFormat.Point10Decimal,
            "POINT_10" => ScoreFormat.Point10,
            "POINT_5" => ScoreFormat.Point5,
            "POINT_3" => ScoreFormat.Point3,
            _ => null
        };
    }

    public async Task<bool> UpdateAnime(int id, AniListSearch.MediaListStatus status, int progress, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null) {
        return await UpdateAnimeReceipt(id, status, progress, numberOfTimesRewatched, startDate, endDate) != null;
    }

    /// <param name="scoreRaw">A score on the 100-point scale to write (0 clears it); null sends no score, as before scores existed.</param>
    internal async Task<UpdateAnimeStatusResponse?> UpdateAnimeReceipt(int id, AniListSearch.MediaListStatus status, int progress, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, int? scoreRaw = null) {
        string query = @"mutation ($mediaId: Int, $status: MediaListStatus, $progress: Int" +
                       (numberOfTimesRewatched != null ? ", $repeat: Int" : "") +
                       (startDate != null ? ",$startDay: Int, $startMonth: Int, $startYear: Int" : "") +
                       (endDate != null ? ",$endDay: Int, $endMonth: Int, $endYear: Int" : "") +
                       (scoreRaw != null ? ", $scoreRaw: Int" : "") +
                       @") {
          SaveMediaListEntry (mediaId: $mediaId, status: $status, progress: $progress" +
                       (numberOfTimesRewatched != null ? ", repeat: $repeat" : "") +
                       (startDate != null ? @", startedAt: {day: $startDay, month: $startMonth, year: $startYear}" : "") +
                       (endDate != null ? @", completedAt: {day: $endDay, month: $endMonth, year: $endYear}" : "") +
                       (scoreRaw != null ? ", scoreRaw: $scoreRaw" : "") +
                       @") {
            id
            progress
            status
            repeat" +
                       (scoreRaw != null ? "\n            score(format: POINT_100)" : "") +
                       @"
          }
        }";

        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "mediaId", id.ToString() },
            { "status", status.ToString().ToUpper() },
            { "progress", progress.ToString() }
        };

        if (numberOfTimesRewatched != null) {
            variables.Add("repeat", numberOfTimesRewatched.Value.ToString());
        }

        if (startDate != null) {
            variables.Add("startDay", startDate.Value.Day.ToString());
            variables.Add("startMonth", startDate.Value.Month.ToString());
            variables.Add("startYear", startDate.Value.Year.ToString());
        }

        if (scoreRaw != null) {
            variables.Add("scoreRaw", scoreRaw.Value.ToString());
        }

        if (endDate != null) {
            variables.Add("endDay", endDate.Value.Day.ToString());
            variables.Add("endMonth", endDate.Value.Month.ToString());
            variables.Add("endYear", endDate.Value.Year.ToString());
        }

        var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.AniList, variables);
        UpdateAnimeStatusResponse? receipt = null;
        bool acknowledged = await MutationAcknowledgement.Validate(response, root => {
            if (!MutationAcknowledgement.Object(root, "data", out var data) ||
                !MutationAcknowledgement.Object(data, "SaveMediaListEntry", out var entry) ||
                !MutationAcknowledgement.Id(entry))
                return false;
            int? returnedProgress = MutationAcknowledgement.Number(entry, "progress", out var actualProgress) && actualProgress >= 0
                ? actualProgress : null;
            Status? returnedStatus = entry.TryGetProperty("status", out var statusField) && statusField.ValueKind == JsonValueKind.String
                ? statusField.GetString() switch {
                    "CURRENT" => Status.Watching,
                    "PLANNING" => Status.Plan_to_watch,
                    "COMPLETED" => Status.Completed,
                    "DROPPED" => Status.Dropped,
                    "PAUSED" => Status.On_hold,
                    "REPEATING" => Status.Rewatching,
                    _ => null
                } : null;
            if (!returnedProgress.HasValue && !returnedStatus.HasValue)
                return false;
            receipt = new UpdateAnimeStatusResponse {
                UsesAcknowledgementFields = true,
                AcknowledgedProgress = returnedProgress,
                AcknowledgedStatus = returnedStatus,
                AcknowledgedRewatching = returnedStatus.HasValue ? returnedStatus == Status.Rewatching : null,
                AcknowledgedRewatchCount = MutationAcknowledgement.Number(entry, "repeat", out var repeat) && repeat >= 0 ? repeat : null,
                AcknowledgedScoreRaw = scoreRaw != null && entry.TryGetProperty("score", out var scoreField) && scoreField.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(scoreField.GetDouble(), MidpointRounding.AwayFromZero) : null
            };
            return true;
        });
        return acknowledged ? receipt : null;
    }

    public async Task<List<AniListMediaList.Entries>?> GetAnimeList(int userId, AniListSearch.MediaListStatus status) {
        string query = @"query ($status: MediaListStatus, $userId: Int, $chunk: Int, $chunkSize: Int) {
              MediaListCollection(userId: $userId, status: $status, chunk: $chunk, perChunk: $chunkSize, type: ANIME) {
                hasNextChunk
                lists {
                  entries {
                    media {
                      id
                      siteUrl
                      title {
                        english
                        romaji
                      }
                    }
                    completedAt {
                      day
                      month
                      year
                    }
                    progress
                  }
                }
              }
            }";

        int page = 1;
        Dictionary<string, object> variables = new Dictionary<string, object> {
            { "status", status.ToString().ToUpper() },
            { "userId", userId.ToString() },
            { "chunkSize", ChunkSize.ToString() },
            { "chunk", page.ToString() }
        };

        AniListMediaList.AniListUserMediaList? result = await GraphQlHelper.DeserializeRequest<AniListMediaList.AniListUserMediaList>(_httpClient, _logger, ApiName.AniList, _memoryCache, _delayer, query, variables);

        if (result?.Data?.MediaListCollection?.MediaList != null) {
            var entriesList = result.Data.MediaListCollection.MediaList.SelectMany(entries => entries.Entries!);
            if (result.Data.MediaListCollection.HasNextChunk == true) {
                // impose a hard limit of 10 pages
                while (page < 10) {
                    page++;
                    variables["chunk"] = page.ToString();

                    AniListMediaList.AniListUserMediaList? nextPageResult = await GraphQlHelper.DeserializeRequest<AniListMediaList.AniListUserMediaList>(_httpClient, _logger, ApiName.AniList, _memoryCache, _delayer, query, variables);
                    // A chunk that failed fails the whole list, as a failed first chunk does.
                    if (nextPageResult == null)
                        return null;

                    AniListMediaList.MediaListCollection next = nextPageResult.Data!.MediaListCollection!;
                    entriesList = entriesList.Concat(next.MediaList!.SelectMany(entries => entries.Entries!));
                    if (next.HasNextChunk != true) {
                        break;
                    }

                    // sleeping thread so we dont hammer the API
                    await _delayer.Delay(TimeSpan.FromSeconds(2));
                }
            }

            return entriesList.ToList();
        }

        return null;
    }
    internal async Task<List<Anime>?> GetWholeAnimeList(int userId, CancellationToken cancellationToken) {
        const string query = """
            query ($userId: Int!, $chunk: Int!, $chunkSize: Int!) {
              MediaListCollection(userId: $userId, chunk: $chunk, perChunk: $chunkSize, type: ANIME) {
                hasNextChunk
                lists {
                  entries {
                    status
                    progress
                    repeat
                    score(format: POINT_100)
                    media { id episodes status nextAiringEpisode { airingAt } }
                  }
                }
              }
            }
            """;
        var entries = new List<Anime>();
        for (int chunk = 1; ; chunk++) {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await GraphQlHelper.AuthenticatedRequest(_httpClientFactory, _loggerFactory, _serverApplicationHost,
                _httpContextAccessor, _memoryCache, _delayer, _userConfig, query, ApiName.AniList,
                new Dictionary<string, object> { ["userId"] = userId, ["chunk"] = chunk, ["chunkSize"] = ChunkSize });
            if (response == null)
                return null;
            using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var page = await JsonSerializer.DeserializeAsync<AniListMediaList.AniListUserMediaList>(body, cancellationToken: cancellationToken);
            if (page?.Data?.MediaListCollection is not { MediaList: { } lists, HasNextChunk: { } hasNext } || page.Errors is { Count: > 0 })
                return null;
            int before = entries.Count;
            foreach (var list in lists) {
                if (list.Entries == null)
                    return null;
                foreach (var row in list.Entries) {
                    if (row.Media is not { } media || row.ListStatus is not { } status)
                        return null;
                    entries.Add(new Anime {
                        Id = media.Id,
                        NumEpisodes = media.Episodes ?? 0,
                        LibraryIsAiring = media.Status == AniListSearch.AiringStatus.RELEASING,
                        NextAiringAtUtc = media.NextAiringEpisode?.AiringAt is > 0 and <= 253402300799 and var timestamp
                            ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : null,
                        MyListStatus = new MyListStatus {
                            Status = status switch {
                                AniListSearch.MediaListStatus.Completed => Status.Completed,
                                AniListSearch.MediaListStatus.Repeating => Status.Rewatching,
                                AniListSearch.MediaListStatus.Dropped => Status.Dropped,
                                AniListSearch.MediaListStatus.Paused => Status.On_hold,
                                AniListSearch.MediaListStatus.Planning => Status.Plan_to_watch,
                                _ => Status.Watching
                            },
                            NumEpisodesWatched = row.Progress ?? 0,
                            RewatchCount = row.Repeat,
                            ScoreRaw = row.Score is >= 1 and <= 100 and var score ? (int)Math.Round(score, MidpointRounding.AwayFromZero) : null
                        }
                    });
                }
            }
            if (!hasNext)
                return entries;
            if (before == entries.Count || chunk == 100)
                return null;
        }
    }

}
