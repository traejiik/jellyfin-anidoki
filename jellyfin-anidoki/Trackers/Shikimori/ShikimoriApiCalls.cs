using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Shikimori;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Api.Shikimori;

public class ShikimoriApiCalls {
    private readonly ILogger<ShikimoriApiCalls> _logger;
    private readonly AuthApiCall _authApiCall;
    private readonly string _apiBaseUrl = ProviderHelpers.GetShikimoriDomain() + "api";
    private readonly UserConfig? _userConfig;
    private readonly Dictionary<string, string>? _requestHeaders;

    private readonly string _graphqlQuery = @"
      query SearchAnime($search: String!, $page: Int!, $limit: Int!) {
        animes(search: $search, page: $page, limit: $limit) {
          ...AnimeFields
        }
      }

      query GetAnime($id: String!, $getRelated: Boolean!) {
        animes(ids: $id) {
          ...AnimeFields

          userRate {
            ...UserRateFields
          }

          related @include(if: $getRelated) {
            anime {
              ...AnimeFields
            }
            relationEn
          }
        }
      }

      query GetUserAnimeList($mylist: MylistString!, $page: Int!, $limit: Int!) {
        animes(mylist: $mylist, page: $page, limit: $limit) {
          ...AnimeFields
          userRate {
            ...UserRateFields
          }
        }
      }

      fragment UserRateFields on UserRate {
         status
         rewatches
         episodes
         score
      }

      fragment AnimeFields on Anime {
        id
        malId
        name
        episodes
        synonyms
        russian
        english
        japanese
        isCensored
      }
    ";

    public ShikimoriApiCalls(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache, IAsyncDelayer delayer, Dictionary<string, string>? requestHeaders, UserConfig? userConfig = null) {
        _userConfig = userConfig;
        _requestHeaders = requestHeaders;
        _logger = loggerFactory.CreateLogger<ShikimoriApiCalls>();
        _authApiCall = new AuthApiCall(httpClientFactory, serverApplicationHost, httpContextAccessor, loggerFactory, memoryCache, delayer, userConfig: userConfig);
    }

    public class User {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("nickname")] public string? Name { get; set; }
    }

    /// <summary>
    /// Get a users information.
    /// </summary>
    public async Task<User?> GetUserInformation() {
        UrlBuilder url = new UrlBuilder {
            Base = $"{_apiBaseUrl}/users/whoami"
        };
        var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Shikimori, AuthApiCall.CallType.GET, url.Build(), requestHeaders: _requestHeaders);
        if (apiCall != null) {
            StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
            string streamText = await streamReader.ReadToEndAsync();

            try {
                User? user = JsonSerializer.Deserialize<User>(streamText);

                if (user != null && _userConfig != null) {
                    // A user linked before any key pair was saved has no list yet.
                    List<KeyPairs> keyPairs = _userConfig.KeyPairs ??= [];
                    string id = user.Id.ToString();
                    // One pair, the current account's: a relinked account must not keep reading the old ID.
                    List<KeyPairs> saved = keyPairs.FindAll(pair => pair.Key == "ShikimoriUserId");
                    if (saved.Count != 1 || saved[0].Value != id) {
                        keyPairs.RemoveAll(pair => pair.Key == "ShikimoriUserId");
                        keyPairs.Add(new KeyPairs {
                            Key = "ShikimoriUserId",
                            Value = id
                        });
                        Plugin.Instance?.SaveConfiguration();
                    }
                }

                return user;
            } catch (Exception) {
                return null;
            }
        } else {
            return null;
        }
    }

    /// <summary>
    /// Search for an anime based upon its name.
    /// </summary>
    /// <param name="searchString">The name to search for.</param>
    /// <returns></returns>
    public async Task<List<ShikimoriAnime>?> SearchAnime(string searchString) {
        const int maxPages = 10;
        const int limit = 50;

        List<ShikimoriAnime>? result = null;
        for (int page = 1; page < maxPages; page++) {
            var request = new GraphqlRequest {
                Query = _graphqlQuery,
                OperationName = "SearchAnime",
                Variables = new Dictionary<string, object>() {
                    { "search", searchString },
                    { "limit", limit },
                    { "page", page },
                },
            };

            var data = await GraphqlApiCall<Dictionary<string, List<ShikimoriAnime>>>(request);
            if (data == null) {
                break;
            }

            if (!data.TryGetValue("animes", out var animes)) {
                _logger.LogWarning("({Provider}) The GraphQL response has no animes field", ApiName.Shikimori);
                break;
            }

            if (result != null) {
                result.AddRange(animes);
            } else {
                result = animes;
            }

            if (animes.Count < limit) {
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Get an anime.
    /// </summary>
    /// <param name="id">ID of the anime you want to get.</param>
    /// <returns></returns>
    public async Task<ShikimoriAnime?> GetAnime(string id, bool getRelated = false) {
        var request = new GraphqlRequest {
            Query = _graphqlQuery,
            OperationName = "GetAnime",
            Variables = new Dictionary<string, object>() {
                { "id", id },
                { "getRelated", getRelated },
            },
        };

        var data = await GraphqlApiCall<Dictionary<string, List<ShikimoriAnime>>>(request);
        if (data == null) {
            return null;
        }

        if (!data.TryGetValue("animes", out var animes)) {
            _logger.LogWarning("({Provider}) The GraphQL response has no animes field", ApiName.Shikimori);
            return null;
        }

        if (!animes.Any()) {
            return null;
        }

        return animes[0];
    }

    /// <summary>
    /// Get a anime users rates.
    /// </summary>
    /// <param name="status">Only retrieve user rate with a given status.</param>
    /// <returns></returns>
    public async Task<List<ShikimoriAnime>?> GetUserAnimeList(ShikimoriUserRate.StatusEnum? status = null) {
        const int limit = 50;

        string mylist;
        if (status == null) {
            mylist = String.Join(
                ",",
                Enum.GetValues(typeof(ShikimoriUserRate.StatusEnum))
                    .Cast<ShikimoriUserRate.StatusEnum>()
                    .Select(x => x.ToString()));
        } else {
            mylist = status.Value.ToString();
        }

        List<ShikimoriAnime>? result = null;
        for (int page = 1; ; page++) {
            var request = new GraphqlRequest {
                Query = _graphqlQuery,
                OperationName = "GetUserAnimeList",
                Variables = new Dictionary<string, object>() {
                    { "page", page },
                    { "limit", limit },
                    { "mylist", mylist },
                },
            };

            var data = await GraphqlApiCall<Dictionary<string, List<ShikimoriAnime>>>(request);
            if (data == null) {
                return null;
            }

            if (!data.TryGetValue("animes", out var animes)) {
                _logger.LogWarning("({Provider}) The GraphQL response has no animes field", ApiName.Shikimori);
                return null;
            }

            if (result != null) {
                result.AddRange(animes);
            } else {
                result = animes;
            }

            if (animes.Count < limit) {
                break;
            }
        }
        return result;
    }

    public async Task<bool> UpdateAnime(string id, ShikimoriUserRate.StatusEnum updateStatus, int progress, int? numberOfTimesRewatched = null) =>
        await UpdateAnimeReceipt(id, updateStatus, progress, numberOfTimesRewatched) != null;

    /// <param name="score">The score, 0–10 (0 clears it); null sends no score.</param>
    /// <returns>The user rate as Shikimori acknowledged it, or null when the write was not confirmed.</returns>
    internal async Task<ShikimoriUserRate?> UpdateAnimeReceipt(string id, ShikimoriUserRate.StatusEnum updateStatus, int progress,
        int? numberOfTimesRewatched = null, int? score = null) {
        UrlBuilder url = new UrlBuilder {
            Base = $"{_apiBaseUrl}/v2/user_rates"
        };

        string? shikimoriUserId = await GetUserId();
        if (shikimoriUserId == null)
            return null;

        ShikimoriUpdate.UpdateBody updateBody = new ShikimoriUpdate.UpdateBody {
            UserRate = new ShikimoriUpdate.UserRate {
                AnimeId = id,
                UserId = shikimoriUserId,
                Episodes = progress,
                Status = updateStatus,
            }
        };

        if (numberOfTimesRewatched != null) {
            updateBody.UserRate.Rewatches = numberOfTimesRewatched.Value;
        }

        if (score != null) {
            updateBody.UserRate.Score = score.Value;
        }

        var jsonSerializerOptions = new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var stringContent = new StringContent(JsonSerializer.Serialize(updateBody, jsonSerializerOptions), Encoding.UTF8, "application/json");
        var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Shikimori, AuthApiCall.CallType.POST, url.Build(), stringContent: stringContent, requestHeaders: _requestHeaders);
        if (apiCall != null) {
            ShikimoriUserRate? acknowledged = null;
            bool confirmed = await MutationAcknowledgement.Validate(apiCall, rate => {
                if (!(MutationAcknowledgement.Id(rate) &&
                      MutationAcknowledgement.Number(rate, "target_id", out var targetId) && targetId.ToString() == id &&
                      MutationAcknowledgement.Text(rate, "target_type", "Anime") &&
                      MutationAcknowledgement.Number(rate, "episodes", out var episodes) && episodes == progress &&
                      MutationAcknowledgement.Text(rate, "status", updateStatus.ToString())))
                    return false;
                acknowledged = new ShikimoriUserRate {
                    Status = updateStatus,
                    Episodes = episodes,
                    Score = MutationAcknowledgement.Number(rate, "score", out var returnedScore) ? returnedScore : null
                };
                return true;
            });
            return confirmed ? acknowledged : null;
        }

        return null;
    }

    private async Task<string?> GetUserId() {
        string? shikimoriUserId = _userConfig?.KeyPairs?.FirstOrDefault(keypair => keypair.Key == "ShikimoriUserId")?.Value;
        if (shikimoriUserId == null || string.IsNullOrEmpty(shikimoriUserId)) {
            _logger.LogDebug("({Provider}) No stored user ID; reading the linked user", ApiName.Shikimori);
            User? user = await GetUserInformation();
            if (user != null) {
                shikimoriUserId = user.Id.ToString();
            } else {
                _logger.LogWarning("({Provider}) Could not read the linked user's ID", ApiName.Shikimori);
                return null;
            }
        }

        return shikimoriUserId;
    }

    private class GraphqlRequest {
        [JsonPropertyName("query")]
        public required string Query { get; set; }
        [JsonPropertyName("operationName")]
        public string? OperationName { get; set; }
        [JsonPropertyName("variables")]
        public Dictionary<string, object>? Variables { get; set; }
    }

    public class GraphqlResponse<DataType> {
        [JsonPropertyName("data")]
        public DataType? Data { get; set; }
        [JsonPropertyName("errors")]
        public List<GraphqlError>? Errors { get; set; }
    }

    public class GraphqlError {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private async Task<T?> GraphqlApiCall<T>(GraphqlRequest request) where T : class {
        var url = new UrlBuilder {
            Base = $"{_apiBaseUrl}/graphql"
        };

        var jsonSerializerOptions = new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // See also https://graphql.org/learn/serving-over-http
        var stringContent = new StringContent(
            JsonSerializer.Serialize(request, jsonSerializerOptions),
            Encoding.UTF8, "application/json");
        var apiCall = await _authApiCall.AuthenticatedApiCall(
            ApiName.Shikimori, AuthApiCall.CallType.POST, url.Build(),
            stringContent: stringContent,
            requestHeaders: _requestHeaders);
        if (apiCall == null) {
            return null;
        }

        GraphqlResponse<T>? response;
        try {
            StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
            response = JsonSerializer.Deserialize<GraphqlResponse<T>>(await streamReader.ReadToEndAsync());
        } catch (Exception e) {
            _logger.LogError(e, "({Provider}) Could not read the GraphQL response", ApiName.Shikimori);
            return null;
        }

        // A body of literal null still throws here, as it always has: a failed read, never "not on the list".
        if (response!.Errors != null && response.Errors.Any()) {
            foreach (GraphqlError error in response.Errors) {
                _logger.LogWarning("({Provider}) The GraphQL request was rejected: {ErrorMessage}", ApiName.Shikimori, error.Message);
            }
            return null;
        }

        if (response.Data == null) {
            _logger.LogWarning("({Provider}) The GraphQL response has no data", ApiName.Shikimori);
            return null;
        }

        return response.Data;
    }
}
