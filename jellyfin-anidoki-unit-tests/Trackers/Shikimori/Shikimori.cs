using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Shikimori;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Trackers.Mal;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers.Shikimori;

public class Shikimori {
    private ShikimoriApiCalls _shikimoriApiCalls = null!;
    private ILoggerFactory _loggerFactory = null!;
    private Mock<IServerApplicationHost> _serverApplicationHost = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    private UserConfig _userConfig = null!;
    private SyncHarness? _harness;

    // Cleared after disposing, so a test that builds no harness does not dispose the previous test's twice.
    [TearDown]
    public void TearDown() {
        _harness?.Dispose();
        _harness = null;
    }

    private void Setup(List<Helpers.HttpCall> httpCalls, bool withKeyPairs = true) {
        // Reading the user saves the configuration, so each test needs a live plugin of its own, not one an earlier
        // fixture left behind.
        _harness = new SyncHarness();
        _loggerFactory = new NullLoggerFactory();
        _serverApplicationHost = new Mock<IServerApplicationHost>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        _shikimoriApiCalls = new ShikimoriApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, memoryCache, mockDelayer.Object, new Dictionary<string, string> { { "User-Agent", "awd" } }, _userConfig = new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.Shikimori,
                    RefreshToken = "refreshToken"
                }
            },
            KeyPairs = withKeyPairs ? new List<KeyPairs>() : null
        });
    }

    [Test]
    public async Task TestGetUser() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users/whoami"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.User {
                    Id = 1
                })
            }
        });
        var result = await _shikimoriApiCalls.GetUserInformation();

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestGenericSearch() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/graphql"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.GraphqlResponse<Dictionary<string, List<ShikimoriAnime>>> {
                    Data = new Dictionary<string, List<ShikimoriAnime>> {
                        {
                            "animes", new List<ShikimoriAnime> {
                                new () {
                                    Id = 1.ToString()
                                }
                            }
                        }
                    }
                })
            }
        });
        var result = await _shikimoriApiCalls.SearchAnime(String.Empty);

        Assert.IsNotNull(result?[0].Id);
    }

    [Test]
    public async Task TestGenericSearchRetrievingMaximumResults() {
        var shikimoriMediaList = new List<ShikimoriAnime>();
        for (int i = 0; i < 50; i++) {
            shikimoriMediaList.Add(new ShikimoriAnime {
                Id = i.ToString()
            });
        }

        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/graphql"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.GraphqlResponse<Dictionary<string, List<ShikimoriAnime>>> {
                    Data = new Dictionary<string, List<ShikimoriAnime>> {
                        {
                            "animes", shikimoriMediaList
                        }
                    }
                })
            }
        });

        var result = await _shikimoriApiCalls.SearchAnime(String.Empty);

        Assert.IsTrue(result?.Count == 450);
    }

    [Test]
    public async Task TestGetAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/graphql"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.GraphqlResponse<Dictionary<string, List<ShikimoriAnime>>> {
                    Data = new Dictionary<string, List<ShikimoriAnime>> {
                        {
                            "animes", new List<ShikimoriAnime> {
                                new () {
                                    Id = 1.ToString()
                                }
                            }
                        }
                    }
                })
            }
        });
        var result = await _shikimoriApiCalls.GetAnime(1.ToString());

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestGetRelatedAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/graphql"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.GraphqlResponse<Dictionary<string, List<ShikimoriAnime>>> {
                    Data = new Dictionary<string, List<ShikimoriAnime>> {
                        {
                            "animes", new List<ShikimoriAnime> {
                                new () {
                                    Id = 1.ToString()
                                }
                            }
                        }
                    }
                })
            }
        });
        var result = await _shikimoriApiCalls.GetAnime(1.ToString(), getRelated: true);

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestUpdateAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users/whoami"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.User {
                    Id = 1
                })
            },
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/user_rates"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new List<ShikimoriUpdate.UserRate> {
                    new () {
                        AnimeId = 1.ToString()
                    }
                })
            },
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/user_rates"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"id\":123,\"target_id\":1,\"target_type\":\"Anime\",\"episodes\":1,\"status\":\"watching\"}"
            }
        });
        var result = await _shikimoriApiCalls.UpdateAnime(1.ToString(), ShikimoriUserRate.StatusEnum.watching, 1, 1);

        Assert.IsTrue(result);
    }

    [Test]
    public async Task TestGetAnimeList() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users/whoami"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.User {
                    Id = 1
                })
            },
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/graphql"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ShikimoriApiCalls.GraphqlResponse<Dictionary<string, List<ShikimoriAnime>>> {
                    Data = new Dictionary<string, List<ShikimoriAnime>> {
                        {
                            "animes", new List<ShikimoriAnime> {
                                new () {
                                    Id = 1.ToString()
                                }
                            }
                        }
                    }
                })
            }
        });
        var result = await _shikimoriApiCalls.GetUserAnimeList();

        Assert.IsNotNull(result);
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"id\":123,\"target_id\":1,\"target_type\":\"Anime\",\"episodes\":1,\"status\":\"watching\"}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Get, ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"id\":1}" },
            new() { RequestMethod = HttpMethod.Post, ResponseCode = responseCode, ResponseContent = body },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _shikimoriApiCalls.UpdateAnime("1", ShikimoriUserRate.StatusEnum.watching, 1), Is.False);
    }

    private static List<Helpers.HttpCall> WhoamiAndUpdate() => new() {
        new() { RequestMethod = HttpMethod.Get, RequestUrlMatch = url => url.EndsWith("/users/whoami"), ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"id\":7,\"nickname\":\"Fixture\"}" },
        new() { RequestMethod = HttpMethod.Get, RequestUrlMatch = url => url.EndsWith("/user_rates"), ResponseCode = HttpStatusCode.OK, ResponseContent = "[]" },
        new() {
            RequestMethod = HttpMethod.Post, RequestUrlMatch = url => url.EndsWith("/user_rates"), ResponseCode = HttpStatusCode.OK,
            ResponseContent = "{\"id\":123,\"target_id\":1,\"target_type\":\"Anime\",\"episodes\":1,\"status\":\"watching\"}"
        }
    };

    /// <summary>A user with no key pairs yet keeps the user Shikimori returns, and its ID is saved for next time.</summary>
    [Test]
    public async Task AUserWithNoKeyPairsYetIsReadAndTheirIdSaved() {
        Setup(WhoamiAndUpdate(), withKeyPairs: false);

        var user = await _shikimoriApiCalls.GetUserInformation();

        Assert.That(user?.Id, Is.EqualTo(7));
        Assert.That(_userConfig.KeyPairs, Has.One.Matches<KeyPairs>(pair => pair.Key == "ShikimoriUserId" && pair.Value == "7"));
    }

    /// <summary>Reading the user again keeps one ID pair, not one more per read.</summary>
    [Test]
    public async Task ReadingTheUserTwiceKeepsOneIdPair() {
        Setup(WhoamiAndUpdate());

        await _shikimoriApiCalls.GetUserInformation();
        await _shikimoriApiCalls.GetUserInformation();

        Assert.That(_userConfig.KeyPairs, Has.Exactly(1).Matches<KeyPairs>(pair => pair.Key == "ShikimoriUserId"));
    }

    /// <summary>After linking another account, the saved ID is the new account's, and older copies are gone.</summary>
    [Test]
    public async Task ReadingTheUserReplacesAStaleId() {
        Setup(WhoamiAndUpdate());
        _userConfig.KeyPairs!.AddRange([
            new KeyPairs { Key = "ShikimoriUserId", Value = "3" },
            new KeyPairs { Key = "ShikimoriUserId", Value = "3" },
            new KeyPairs { Key = "Other", Value = "kept" }
        ]);

        await _shikimoriApiCalls.GetUserInformation();

        Assert.That(_userConfig.KeyPairs, Has.Exactly(1).Matches<KeyPairs>(pair => pair.Key == "ShikimoriUserId"));
        Assert.That(_userConfig.KeyPairs, Has.One.Matches<KeyPairs>(pair => pair.Key == "ShikimoriUserId" && pair.Value == "7"));
        Assert.That(_userConfig.KeyPairs, Has.One.Matches<KeyPairs>(pair => pair.Key == "Other" && pair.Value == "kept"));
    }

    [Test]
    public async Task AnUpdateForAUserWithNoKeyPairsYetReadsTheirIdFromShikimori() {
        Setup(WhoamiAndUpdate(), withKeyPairs: false);

        Assert.That(await _shikimoriApiCalls.UpdateAnime("1", ShikimoriUserRate.StatusEnum.watching, 1), Is.True);
    }

    private static ShikimoriApiCalls SignedInApi(IHttpClientFactory factory) =>
        new(factory, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new Dictionary<string, string> { { "User-Agent", "awd" } },
            new UserConfig {
                UserApiAuth = [new UserApiAuth { AccessToken = "accessToken", Name = ApiName.Shikimori, RefreshToken = "refreshToken" }],
                KeyPairs = [new KeyPairs { Key = "ShikimoriUserId", Value = "7" }]
            });

    private static string RatedAnime(string score) =>
        "{\"data\":{\"animes\":[{\"id\":\"500\",\"malId\":\"500\",\"name\":\"Fixture Show\",\"episodes\":12,\"userRate\":{\"status\":\"watching\",\"rewatches\":0,\"episodes\":3,\"score\":" + score + "}}]}}";

    private static string SavedRate(string score) =>
        "{\"id\":1,\"target_id\":500,\"target_type\":\"Anime\",\"episodes\":5,\"status\":\"watching\",\"rewatches\":0,\"score\":" + score + "}";

    private static System.Text.Json.JsonElement SentRate(Helpers.SentRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("user_rate");

    [TestCase("8", 80)]
    [TestCase("0", null)]
    public async Task TheScoreIsReadAsTenPoint(string score, int? raw) {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Shikimori.ShikimoriTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, RatedAnime(score)))));

        var anime = await client.GetAnime(500, "500");

        Assert.That(sent.Single().Body, Does.Contain("score"));
        Assert.That(anime!.MyListStatus!.ScoreRaw, Is.EqualTo(raw));
    }

    [Test]
    public async Task AScoreIsSent() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Shikimori.ShikimoriTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedRate("9")))));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching, alternativeId: "500", scoreRaw: 85);

        Assert.That(SentRate(sent.Single()).GetProperty("score").GetInt32(), Is.EqualTo(9));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(90));
    }

    [Test]
    public async Task ClearingSendsZero() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Shikimori.ShikimoriTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedRate("0")))));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching, alternativeId: "500", clearScore: true);

        Assert.That(SentRate(sent.Single()).GetProperty("score").GetInt32(), Is.EqualTo(0));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(0));
    }

    [Test]
    public async Task AWriteWithoutAScoreHasNoScoreKey() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Shikimori.ShikimoriTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedRate("7")))));

        var response = await client.UpdateAnime(500, 5, jellyfin_anidoki.Trackers.Mal.Status.Watching, alternativeId: "500");

        Assert.That(SentRate(sent.Single()).TryGetProperty("score", out _), Is.False);
        Assert.That(response!.AcknowledgedScoreRaw, Is.Null);
    }
}
