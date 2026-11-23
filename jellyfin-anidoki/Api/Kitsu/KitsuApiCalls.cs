using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Kitsu;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Api.Kitsu;

/// <remarks>
/// A 200 reply without its data still throws where it is read, as it always has: the pipeline then treats the read as
/// failed. Returning null there would read as "not on the list" and could lead to a write.
/// </remarks>
public class KitsuApiCalls {
    private readonly string ApiUrl = "https://kitsu.io/api/edge";
    private readonly ILogger<KitsuApiCalls> _logger;
    private readonly AuthApiCall _authApiCall;
    private readonly UserConfig _userConfig;

    public KitsuApiCalls(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache, IAsyncDelayer delayer, UserConfig userConfig) {
        _logger = loggerFactory.CreateLogger<KitsuApiCalls>();
        _authApiCall = new AuthApiCall(httpClientFactory, serverApplicationHost, httpContextAccessor, loggerFactory, memoryCache, delayer, userConfig: userConfig);
        _userConfig = userConfig;
    }

    public async Task<List<KitsuSearch.KitsuAnime>?> SearchAnime(string? query) {
        UrlBuilder url = new UrlBuilder {
            Base = $"{ApiUrl}/anime"
        };

        if (query != null) {
            url.Parameters.Add(new KeyValuePair<string, string>("filter[text]", query));
        }

        string builtUrl = url.Build();
        _logger.LogDebug("({Provider}) Searching for anime (GET {Url})", ApiName.Kitsu, builtUrl);
        var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, AuthApiCall.CallType.GET, builtUrl);
        if (apiCall != null) {
            StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
            var animeList = JsonSerializer.Deserialize<KitsuSearch.KitsuSearchMedia>(await streamReader.ReadToEndAsync());

            _logger.LogDebug("({Provider}) Search finished", ApiName.Kitsu);
            return animeList!.KitsuSearchData;
        }

        return null;
    }

    public async Task<int?> GetUserId() {
        // A user linked before their Kitsu ID was saved has no key pairs yet.
        var userIdKeyPair = _userConfig.KeyPairs?.FirstOrDefault(item => item.Key == "KitsuUserId")?.Value;
        if (userIdKeyPair != null) {
            return int.Parse(userIdKeyPair);
        } else {
            var userInformation = await GetUserInformation();
            if (userInformation != null)
                return userInformation.Id;
        }

        return null;
    }

    public async Task<MalApiCalls.User?> GetUserInformation() {
        UrlBuilder url = new UrlBuilder {
            Base = $"{ApiUrl}/users",
            Parameters = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>("filter[self]", "true")
            }
        };

        _logger.LogDebug("({Provider}) Reading the linked user", ApiName.Kitsu);
        var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, AuthApiCall.CallType.GET, url.Build());
        if (apiCall != null) {
            StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
            var animeList = JsonSerializer.Deserialize<KitsuGetUser.KitsuUserRoot>(await streamReader.ReadToEndAsync());

            _logger.LogDebug("({Provider}) Read the linked user", ApiName.Kitsu);
            KitsuGetUser.KitsuUserData user = animeList!.KitsuUserList![0];
            return new MalApiCalls.User { Id = user.Id, Name = user.KitsuUser!.Name };
        }

        return null;
    }

    public async Task<KitsuGet.KitsuGetAnime?> GetAnime(int animeId) {
        UrlBuilder url = new UrlBuilder {
            Base = $"{ApiUrl}/anime/{animeId}",
        };

        string builtUrl = url.Build();
        _logger.LogDebug("({Provider}) Reading anime {AnimeId} (GET {Url})", ApiName.Kitsu, animeId, builtUrl);
        try {
            var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, AuthApiCall.CallType.GET, builtUrl);
            if (apiCall != null) {
                StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
                var anime = JsonSerializer.Deserialize<KitsuGet.KitsuGetAnime>(await streamReader.ReadToEndAsync());
                _logger.LogDebug("({Provider}) Read anime {AnimeId}", ApiName.Kitsu, animeId);
                List<KitsuSearch.KitsuAnime>? relatedAnime = await GetRelatedAnime(animeId);
                if (relatedAnime != null && anime != null) {
                    anime.KitsuAnimeData!.RelatedAnime = relatedAnime;
                }

                return anime;
            }
        } catch (Exception e) {
            _logger.LogError(e, "({Provider}) Could not read anime {AnimeId}", ApiName.Kitsu, animeId);
        }

        return null;
    }

    public async Task<List<KitsuSearch.KitsuAnime>?> GetRelatedAnime(int animeId) {
        UrlBuilder url = new UrlBuilder {
            Base = $"{ApiUrl}/anime/{animeId}/media-relationships"
        };

        url.Parameters.Add(new KeyValuePair<string, string>("include", "destination"));

        string builtUrl = url.Build();
        try {
            var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, AuthApiCall.CallType.GET, builtUrl);
            if (apiCall != null) {
                StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
                var mediaRelationships = JsonSerializer.Deserialize<KitsuMediaRelationship.MediaRelationship>(await streamReader.ReadToEndAsync());

                List<int> listOfRelatedAnimeIds = new List<int>();
                if (mediaRelationships != null) {
                    foreach (KitsuMediaRelationship.RelationshipData relationshipData in mediaRelationships.Data!) {
                        KitsuMediaRelationship.RelationshipData destination = relationshipData.Relationships!.Destination!.RelationshipData!;
                        if (destination.Type == "anime") {
                            listOfRelatedAnimeIds.Add(int.Parse(destination.Id!));
                        }
                    }

                    // Kitsu leaves out "included" when the anime has no related entries.
                    if (mediaRelationships.Included == null)
                        return [];

                    for (var i = 0; i < mediaRelationships.Included.Count; i++) {
                        mediaRelationships.Included[i].RelationType = mediaRelationships.Data[i].Attributes!.RelationType;
                    }

                    return mediaRelationships.Included.Where(anime => listOfRelatedAnimeIds.Any(ids => anime.Id == ids)).ToList();
                }
            }
        } catch (Exception e) {
            _logger.LogError(e, "({Provider}) Could not read the anime related to {AnimeId}", ApiName.Kitsu, animeId);
        }

        return null;
    }

    public async Task<bool> UpdateAnimeStatus(int animeId, int numberOfWatchedEpisodes, KitsuUpdate.Status status,
        bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null) =>
        await UpdateAnimeStatusReceipt(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched, startDate, endDate) != null;

    /// <param name="ratingTwenty">A rating to set, 2–20; null leaves it alone.</param>
    /// <param name="clearRating">Remove the rating (sent as an explicit null).</param>
    /// <returns>The library entry's attributes as Kitsu acknowledged them, or null when the write was not confirmed.</returns>
    internal async Task<KitsuUpdate.Attributes?> UpdateAnimeStatusReceipt(int animeId, int numberOfWatchedEpisodes, KitsuUpdate.Status status,
        bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null,
        int? ratingTwenty = null, bool clearRating = false) {
        _logger.LogDebug("({Provider}) Preparing to update anime {AnimeId}", ApiName.Kitsu, animeId);
        int? userId = await GetUserId();

        if (userId != null) {
            var libraryStatus = await GetUserAnimeStatus(userId.Value, animeId);
            // if this is populated, it means there is already a record of the anime in the users anime list. therefore we update the record instead of create a new one
            UrlBuilder url = new UrlBuilder {
                Base = $"{ApiUrl}/library-entries{(libraryStatus != null ? $"/{libraryStatus.Id}" : "")}"
            };

            var payload = new KitsuUpdate.KitsuLibraryEntryPostPatchRoot {
                Data = new KitsuUpdate.KitsuLibraryEntry {
                    Type = "libraryEntries",
                    Attributes = new KitsuUpdate.Attributes {
                        Status = status,
                        Progress = numberOfWatchedEpisodes
                    },
                    Relationships = new KitsuUpdate.Relationships {
                        AnimeData = new KitsuUpdate.AnimeData {
                            Anime = new KitsuSearch.KitsuAnime {
                                Type = "anime",
                                Id = animeId
                            }
                        },
                        UserData = new KitsuUpdate.UserData {
                            User = new KitsuGetUser.KitsuUser {
                                Type = "users",
                                Id = userId.Value
                            }
                        }
                    }
                }
            };

            if (libraryStatus != null) {
                payload.Data.Id = libraryStatus.Id;
            }

            if (isRewatching != null) {
                payload.Data.Attributes.Reconsuming = isRewatching.Value;
            }

            if (numberOfTimesRewatched != null) {
                payload.Data.Attributes.ReconsumeCount = numberOfTimesRewatched.Value;
            }

            if (startDate != null) {
                payload.Data.Attributes.StartedAt = startDate.Value;
            }

            if (endDate != null) {
                payload.Data.Attributes.FinishedAt = endDate.Value;
            }

            if (ratingTwenty != null) {
                payload.Data.Attributes.RatingTwenty = ratingTwenty.Value;
            }

            var jsonSerializerOptions = new JsonSerializerOptions {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(payload, jsonSerializerOptions);
            if (clearRating) {
                // Nulls are left out of the payload, so the explicit null that clears a rating is added afterwards.
                JsonNode document = JsonNode.Parse(json)!;
                document["data"]!["attributes"]!["ratingTwenty"] = null;
                json = document.ToJsonString();
            }

            var stringContent = new StringContent(json, Encoding.UTF8, "application/vnd.api+json");
            HttpResponseMessage? apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, libraryStatus != null ? AuthApiCall.CallType.PATCH : AuthApiCall.CallType.POST, url.Build(), stringContent: stringContent);

            if (apiCall != null) {
                KitsuUpdate.Attributes? acknowledged = null;
                bool confirmed = await MutationAcknowledgement.Validate(apiCall, root => {
                    if (!(MutationAcknowledgement.Object(root, "data", out var entry) &&
                          MutationAcknowledgement.Id(entry) && MutationAcknowledgement.Text(entry, "type", "libraryEntries") &&
                          MutationAcknowledgement.Object(entry, "attributes", out var attributes) &&
                          MutationAcknowledgement.Number(attributes, "progress", out var progress) && progress == numberOfWatchedEpisodes &&
                          MutationAcknowledgement.Text(attributes, "status", status.ToString())))
                        return false;
                    acknowledged = new KitsuUpdate.Attributes {
                        Status = status,
                        Progress = progress,
                        RatingTwenty = MutationAcknowledgement.Number(attributes, "ratingTwenty", out var rating) ? rating : null
                    };
                    return true;
                });
                return confirmed ? acknowledged : null;
            }
        }

        return null;
    }

    public async Task<KitsuUpdate.KitsuLibraryEntry?> GetUserAnimeStatus(int userId, int animeId) {
        KitsuUpdate.KitsuLibraryEntryListRoot? animeList = await LibraryEntriesCall(new List<KeyValuePair<string, string>> {
            new ("filter[animeId]", animeId.ToString()),
            new ("filter[userId]", userId.ToString())
        });

        // A read that failed throws here rather than reading as "no entry", which would make an update add a second one.
        if (animeList!.Data is { Count: > 0 }) {
            return animeList.Data[0];
        }

        return null;
    }

    private async Task<KitsuUpdate.KitsuLibraryEntryListRoot?> LibraryEntriesCall(List<KeyValuePair<string, string>> parameters) {
        UrlBuilder url = new UrlBuilder {
            Base = $"{ApiUrl}/library-entries",
            Parameters = parameters
        };

        _logger.LogDebug("({Provider}) Reading the user's anime list", ApiName.Kitsu);
        try {
            var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.Kitsu, AuthApiCall.CallType.GET, url.Build());

            if (apiCall != null) {
                StreamReader streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
                var library = JsonSerializer.Deserialize<KitsuUpdate.KitsuLibraryEntryListRoot>(await streamReader.ReadToEndAsync());
                _logger.LogDebug("({Provider}) Read the user's anime list", ApiName.Kitsu);
                return library;
            }
        } catch (Exception e) {
            _logger.LogError(e, "({Provider}) Could not read the user's anime list", ApiName.Kitsu);
        }

        return null;
    }

    /// <summary>
    /// Every entry of the user's list with this status, page by page until Kitsu names no next page (at most
    /// <see cref="MaxListPages"/> pages of <see cref="ListPageSize"/>). Null when any page cannot be read, so a sync
    /// never works from part of the list.
    /// </summary>
    public async Task<KitsuUpdate.KitsuLibraryEntryListRoot?> GetUserAnimeList(int userId, KitsuUpdate.Status status) {
        var all = new KitsuUpdate.KitsuLibraryEntryListRoot { Data = new List<KitsuUpdate.KitsuLibraryEntry>(), Included = new List<KitsuSearch.KitsuAnime>() };
        for (int page = 0; page < MaxListPages; page++) {
            KitsuUpdate.KitsuLibraryEntryListRoot? animeList = await LibraryEntriesCall(new List<KeyValuePair<string, string>> {
                new("filter[userId]", userId.ToString()),
                new("filter[kind]", "anime"),
                new("filter[status]", status.ToString()),
                new("include", "anime"),
                new("page[limit]", ListPageSize.ToString()),
                new("page[offset]", (page * ListPageSize).ToString()),
            });
            if (animeList?.Data == null)
                return null;
            all.Data.AddRange(animeList.Data);
            all.Included.AddRange(animeList.Included ?? []);
            if (string.IsNullOrEmpty(animeList.Links?.Next) || animeList.Data.Count == 0)
                return all;
        }

        _logger.LogWarning("({Provider}) Read the first {Count} anime list entries; the rest were not read", ApiName.Kitsu, MaxListPages * ListPageSize);
        return all;
    }

    // Kitsu allows library entries up to 500 a page.
    private const int ListPageSize = 500;
    private const int MaxListPages = 10;
}
