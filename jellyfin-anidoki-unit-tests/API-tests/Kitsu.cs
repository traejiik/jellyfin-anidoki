using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Kitsu;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class Kitsu {
    private KitsuApiCalls _kitsuApiCalls = null!;
    private ILoggerFactory _loggerFactory = null!;
    private Mock<IServerApplicationHost> _serverApplicationHost = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private MemoryCache _memoryCache = null!;
    private Mock<IAsyncDelayer> _mockDelayer = null!;

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        _loggerFactory = new NullLoggerFactory();
        _serverApplicationHost = new Mock<IServerApplicationHost>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _mockDelayer = new Mock<IAsyncDelayer>();
        _kitsuApiCalls = new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, _memoryCache, _mockDelayer.Object, new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.Kitsu,
                    RefreshToken = "refreshToken"
                }
            },
            KeyPairs = new List<KeyPairs> {
                new KeyPairs {
                    Key = "KitsuUserId",
                    Value = 1.ToString()
                }
            }
        });
    }

    [Test]
    public async Task TestGenericSearch() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/anime"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuSearch.KitsuSearchMedia {
                    KitsuSearchData = new List<KitsuSearch.KitsuAnime> {
                        new()  {
                            Id = 1
                        }
                    }
                })
            }
        });
        var result = await _kitsuApiCalls.SearchAnime(String.Empty);

        Assert.IsNotNull(result![0].Id);
    }

    [Test]
    public async Task TestGetUserInformation() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuGetUser.KitsuUserRoot {
                    KitsuUserList = new List<KitsuGetUser.KitsuUserData> {
                        new () {
                            Id = 1,
                            KitsuUser = new KitsuGetUser.KitsuUser {
                                Id = 2,
                                Name = String.Empty
                            }
                        }
                    }
                })
            }
        });
        var result = await _kitsuApiCalls.GetUserInformation();

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestGetAnime() {
        int id = 1;
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith($"/anime/{id}"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuGet.KitsuGetAnime {
                    KitsuAnimeData = new KitsuSearch.KitsuAnime {
                        Id = id
                    }
                })
            }
        });
        var result = await _kitsuApiCalls.GetAnime(id);

        Assert.IsNotNull(result!.KitsuAnimeData!.Id);
    }

    [Test]
    public async Task TestGetUserList() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("library-entries"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuUpdate.KitsuLibraryEntryListRoot {
                    Data = new List<KitsuUpdate.KitsuLibraryEntry> {
                        new () {
                            Id = 1,
                            Attributes = new KitsuUpdate.Attributes {
                                Progress = 1
                            }
                        }
                    }
                })
            }
        });
        var result = await _kitsuApiCalls.GetUserAnimeStatus(1, 1);

        Assert.IsNotNull(result!.Attributes!.Progress);
    }

    private static string ListPage(int animeId, string? next) => $$"""
        {
          "data": [ { "id": "{{animeId}}", "type": "libraryEntries", "attributes": { "status": "current", "progress": 3 },
                      "relationships": { "anime": { "data": { "type": "anime", "id": "{{animeId}}" } } } } ],
          "included": [ { "id": "{{animeId}}", "type": "anime", "attributes": { "canonicalTitle": "Title {{animeId}}" } } ],
          "links": { "next": {{(next == null ? "null" : $"\"{next}\"")}} }
        }
        """;

    [Test]
    public async Task TheUserListIsReadPageByPageUntilKitsuNamesNoNextPage() {
        var requested = new List<string>();
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUriMatch = uri => { if (!uri.ToString().EndsWith("page[offset]=0")) return false; requested.Add(uri.ToString()); return true; },
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = ListPage(500, "https://kitsu.io/api/edge/library-entries?page[offset]=500")
            },
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUriMatch = uri => { if (!uri.ToString().EndsWith("page[offset]=500")) return false; requested.Add(uri.ToString()); return true; },
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = ListPage(501, null)
            }
        });

        var result = await _kitsuApiCalls.GetUserAnimeList(7, KitsuUpdate.Status.current);

        Assert.That(result!.Data!.Select(entry => entry.Relationships!.AnimeData!.Anime!.Id), Is.EqualTo(new[] { 500, 501 }));
        Assert.That(result.Included!.Select(anime => anime.Attributes!.CanonicalTitle), Is.EqualTo(new[] { "Title 500", "Title 501" }));
        Assert.That(requested, Has.All.Contains("page[limit]=500"));
    }

    [Test]
    public async Task AUserListWithAnUnreadablePageIsNotReadAtAll() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUriMatch = uri => uri.ToString().EndsWith("page[offset]=0"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = ListPage(500, "https://kitsu.io/api/edge/library-entries?page[offset]=500")
            },
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUriMatch = uri => uri.ToString().EndsWith("page[offset]=500"),
                ResponseCode = HttpStatusCode.InternalServerError,
                ResponseContent = "{}"
            }
        });

        Assert.That(await _kitsuApiCalls.GetUserAnimeList(7, KitsuUpdate.Status.current), Is.Null);
    }

    [Test]
    public async Task TestUpdateAnimeStatusWithoutFetchingUserId() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.Contains("library-entries"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuUpdate.KitsuLibraryEntryListRoot {
                    Data = new List<KitsuUpdate.KitsuLibraryEntry> {
                        new () {
                            Id = 1,
                            Attributes = new KitsuUpdate.Attributes {
                                Progress = 1
                            }
                        }
                    }
                })
            },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"id\":\"1\",\"type\":\"libraryEntries\",\"attributes\":{\"progress\":1,\"status\":\"current\"}}}" }
        });
        var result = await _kitsuApiCalls.UpdateAnimeStatus(1,
            1,
            KitsuUpdate.Status.current,
            isRewatching: true,
            numberOfTimesRewatched: 1,
            startDate: DateTime.UtcNow - TimeSpan.FromHours(1),
            endDate: DateTime.UtcNow);

        Assert.IsTrue(result);
    }

    [Test]
    public async Task TestUpdateAnimeStatusAndFetchUserId() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuGetUser.KitsuUserRoot {
                    KitsuUserList = new List<KitsuGetUser.KitsuUserData> {
                        new () {
                            Id = 1,
                            KitsuUser = new KitsuGetUser.KitsuUser {
                                Id = 2,
                                Name = String.Empty
                            }
                        }
                    }
                })
            },
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.Contains("library-entries"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuUpdate.KitsuLibraryEntryListRoot {
                    Data = new List<KitsuUpdate.KitsuLibraryEntry> {
                        new () {
                            Id = 1,
                            Attributes = new KitsuUpdate.Attributes {
                                Progress = 1
                            }
                        }
                    }
                })
            },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"id\":\"1\",\"type\":\"libraryEntries\",\"attributes\":{\"progress\":1,\"status\":\"current\"}}}" }
        });

        var result = await _kitsuApiCalls.UpdateAnimeStatus(1,
            1,
            KitsuUpdate.Status.current,
            isRewatching: true,
            numberOfTimesRewatched: 1,
            startDate: DateTime.UtcNow - TimeSpan.FromHours(1),
            endDate: DateTime.UtcNow);

        Assert.IsTrue(result);
    }

    [Test]
    public async Task TestGetRelatedAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("media-relationships"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuMediaRelationship.MediaRelationship {
                    Data = new List<KitsuMediaRelationship.RelationshipData> {
                        new () {
                            Id = 1.ToString(),
                            Relationships = new KitsuMediaRelationship.Relationships {
                                Destination = new KitsuMediaRelationship.Destination {
                                    RelationshipData = new KitsuMediaRelationship.RelationshipData {
                                        Id = 1.ToString(),
                                        Type = "anime"
                                    }
                                }
                            },
                            Attributes = new KitsuMediaRelationship.Attributes {
                                RelationType = KitsuMediaRelationship.RelationType.sequel
                            }
                        }
                    },
                    Included = new List<KitsuSearch.KitsuAnime> {
                        new () {
                            Id = 1,
                            RelationType = KitsuMediaRelationship.RelationType.sequel
                        }
                    }
                })
            }
        });
        var result = await _kitsuApiCalls.GetRelatedAnime(1);

        Assert.IsNotNull(result);
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"data\":{\"id\":\"1\",\"type\":\"libraryEntries\",\"attributes\":{\"progress\":1,\"status\":\"current\"}}}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Get, ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"data\":[]}" },
            new() { RequestMethod = HttpMethod.Post, ResponseCode = responseCode, ResponseContent = body },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _kitsuApiCalls.UpdateAnimeStatus(1, 1, KitsuUpdate.Status.current), Is.False);
    }

    private static KitsuApiCalls Api(IHttpClientFactory factory, List<KeyPairs>? keyPairs) =>
        new(factory, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = [new UserApiAuth { Name = ApiName.Kitsu, AccessToken = "accessToken", RefreshToken = "refreshToken" }],
                KeyPairs = keyPairs
            });

    /// <summary>A user linked before their Kitsu ID was saved has no key pairs yet; the ID is read from Kitsu instead.</summary>
    [Test]
    public async Task AUserWithNoKeyPairsYetReadsTheirIdFromKitsu() {
        var api = Api(Helpers.Sequence((HttpStatusCode.OK, """{"data":[{"id":"42","attributes":{"name":"Fixture"}}]}""")), keyPairs: null);

        Assert.That(await api.GetUserId(), Is.EqualTo(42));
    }

    [Test]
    public async Task RelationsUseDestinationIdentityWhenIncludedResourcesAreReordered() {
        const string payload = """
            {
              "data": [
                {"attributes":{"role":"prequel"},"relationships":{"destination":{"data":{"type":"anime","id":"7"}}}},
                {"attributes":{"role":"alternative_version"},"relationships":{"destination":{"data":{"type":"anime","id":"8"}}}},
                {"attributes":{"role":"adaptation"},"relationships":{"destination":{"data":{"type":"manga","id":"7"}}}}
              ],
              "included": [
                {"type":"manga","id":"7","attributes":{"canonicalTitle":"Manga"}},
                {"type":"anime","id":"8","attributes":{"canonicalTitle":"Remake"}},
                {"type":"anime","id":"7","attributes":{"canonicalTitle":"Prequel","posterImage":{"small":"https://example.test/kitsu.jpg"}}}
              ]
            }
            """;
        var api = Api(Helpers.Sequence((HttpStatusCode.OK, payload)), keyPairs: []);
        var related = await api.GetRelatedAnime(1);
        Assert.That(related?.Select(item => (item.Id, item.RelationType)), Is.EqualTo(new[] {
            (7, (KitsuMediaRelationship.RelationType?)KitsuMediaRelationship.RelationType.prequel),
            (8, (KitsuMediaRelationship.RelationType?)KitsuMediaRelationship.RelationType.alternative_version)
        }));
        Assert.That(jellyfin_anidoki.Trackers.ClassConversions.ConvertKitsuAnime(related!.First()).MainPicture?.Medium,
            Is.EqualTo("https://example.test/kitsu.jpg"));
    }

    /// <summary>Kitsu leaves out "included" when an anime has no related anime; that reads as none, not as an error.</summary>
    [Test]
    public async Task AnAnimeWithNoRelationsReadsAsNone() {
        var api = Api(Helpers.Sequence((HttpStatusCode.OK, """{"data":[]}""")), keyPairs: []);

        Assert.That(await api.GetRelatedAnime(1), Is.Empty);
    }

    private static readonly List<KeyPairs> KitsuUser = [new KeyPairs { Key = "KitsuUserId", Value = "1" }];

    private static string LibraryEntry(string rating) =>
        "{\"data\":[{\"id\":\"9\",\"type\":\"libraryEntries\",\"attributes\":{\"status\":\"current\",\"progress\":3,\"reconsuming\":false,\"reconsumeCount\":0,\"ratingTwenty\":" + rating + "}}]}";

    private static string SavedEntry(string rating) =>
        "{\"data\":{\"id\":\"9\",\"type\":\"libraryEntries\",\"attributes\":{\"status\":\"current\",\"progress\":5,\"ratingTwenty\":" + rating + "}}}";

    private static System.Text.Json.JsonElement SentAttributes(Helpers.SentRequest request) =>
        System.Text.Json.JsonDocument.Parse(request.Body!).RootElement.GetProperty("data").GetProperty("attributes");

    [TestCase("17", 85)]
    [TestCase("null", null)]
    public async Task TheRatingIsReadFromRatingTwenty(string rating, int? raw) {
        var client = new jellyfin_anidoki.Trackers.Kitsu.KitsuTrackerClient(Api(Helpers.Sequence((HttpStatusCode.OK, LibraryEntry(rating))), KitsuUser));

        var status = await client.GetConvertedUserList(500);

        Assert.That(status.ScoreRaw, Is.EqualTo(raw));
    }

    [Test]
    public async Task AScoreIsSentAsRatingTwenty() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Kitsu.KitsuTrackerClient(Api(Helpers.RecordedSequence(sent,
            (HttpStatusCode.OK, LibraryEntry("null")), (HttpStatusCode.OK, SavedEntry("17"))), KitsuUser));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching, scoreRaw: 85);

        Assert.That(SentAttributes(sent[1]).GetProperty("ratingTwenty").GetInt32(), Is.EqualTo(17));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(85));
    }

    [Test]
    public async Task ClearingSendsAnExplicitNull() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Kitsu.KitsuTrackerClient(Api(Helpers.RecordedSequence(sent,
            (HttpStatusCode.OK, LibraryEntry("17")), (HttpStatusCode.OK, SavedEntry("null"))), KitsuUser));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching, clearScore: true);

        Assert.That(SentAttributes(sent[1]).TryGetProperty("ratingTwenty", out var rating), Is.True);
        Assert.That(rating.ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(0));
    }

    [Test]
    public async Task AWriteWithoutAScoreHasNoRatingKey() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Kitsu.KitsuTrackerClient(Api(Helpers.RecordedSequence(sent,
            (HttpStatusCode.OK, LibraryEntry("17")), (HttpStatusCode.OK, SavedEntry("17"))), KitsuUser));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching);

        Assert.That(SentAttributes(sent[1]).TryGetProperty("ratingTwenty", out _), Is.False);
        Assert.That(response!.AcknowledgedScoreRaw, Is.Null);
    }
}
