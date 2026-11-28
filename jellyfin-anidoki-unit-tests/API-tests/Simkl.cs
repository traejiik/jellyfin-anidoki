using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class Simkl {
    private SimklApiCalls _simklApiCalls = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    public void Setup(List<Helpers.HttpCall> httpCalls) {
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        var mockLoggerFactory = new NullLoggerFactory();
        var mockServerApplicationHost = new Mock<IServerApplicationHost>();
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        _simklApiCalls = new SimklApiCalls(_httpClientFactory, mockLoggerFactory, mockServerApplicationHost.Object, mockHttpContextAccessor.Object, memoryCache, mockDelayer.Object, new Dictionary<string, string> { { "simkl-api-key", String.Empty } }, new UserConfig {
            UserApiAuth = new[]{new UserApiAuth {
                Name = ApiName.Simkl,
                AccessToken = String.Empty
            }}
        });
    }

    [Test]
    public async Task TestAuthCall() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/activities"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = String.Empty
            }
        });
        var result = await _simklApiCalls.GetLastActivity();

        Assert.IsTrue(result);
    }

    [Test]
    public async Task TestSearchAnime() {
        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/search/anime"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new List<SimklMedia> {
                    new() {
                        Title = String.Empty
                    }
                })
            }
        });

        var result = await _simklApiCalls.SearchAnime("monogatari");

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result!);
    }

    /// <summary>Simkl needs its API key on every request, so a search longer than one page sends it on the later pages too.</summary>
    [Test]
    public async Task SearchSendsTheApiKeyOnEveryPage() {
        var sent = new List<Helpers.SentRequest>();
        string FullPage() => JsonSerializer.Serialize(new List<SimklMedia>(new SimklMedia[50]).ConvertAll(_ => new SimklMedia { Title = "Fixture" }));
        var api = new SimklApiCalls(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, FullPage()), (HttpStatusCode.OK, "[{\"title\":\"Last\"}]")),
            new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new Dictionary<string, string> { { "simkl-api-key", "client" } },
            new UserConfig { UserApiAuth = [new UserApiAuth { Name = ApiName.Simkl, AccessToken = String.Empty }] });

        var result = await api.SearchAnime("monogatari");

        Assert.That(result, Has.Count.EqualTo(51));
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(sent, Has.All.Matches<Helpers.SentRequest>(request => request.Headers.ContainsKey("simkl-api-key")));
    }

    [Test]
    public async Task TestGetAnime() {
        int animeId = 1;
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith($"/anime/{animeId}"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new SimklExtendedMedia {
                    Title = String.Empty
                })
            }
        });

        var result = await _simklApiCalls.GetAnime(animeId);

        Assert.IsNotNull(result?.Title);
    }

    [Test]
    public async Task TestGetUserList() {
        SimklStatus status = SimklStatus.plantowatch;

        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith($"/anime/{status}"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new SimklUserList {
                    Entry = new List<SimklUserEntry> {
                        new () {

                        }
                    }
                })
            }
        });

        var result = await _simklApiCalls.GetUserAnimeList(status);

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result!);
    }

    [Test]
    public async Task TestGetAnimeByIdLookup() {
        int id = 1;
        string title = "Bakemonogatari";
        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/search/id"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new List<SimklIdLookupMedia> {
                    new() {
                        Title = title
                    }
                })
            }
        });

        var result = await _simklApiCalls.GetAnimeByIdLookup(new ArmClient.OfflineDatabaseResponse {
            MyAnimeList = id
        }, title);

        Assert.IsNotNull(result?.Title);
    }

    [Test]
    public async Task TestUpdateAnime() {
        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.EndsWith("/sync/history"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}"
            }
        });

        var result = await _simklApiCalls.UpdateAnime(45006, SimklStatus.plantowatch, true, new ArmClient.OfflineDatabaseResponse {
            AniDb = 6327
        }, 10);

        Assert.IsTrue(result);
    }

    /// <summary>The admin's "update all episodes" switch decides whether a write sends every episode up to the watched one, or only that one.</summary>
    [TestCase(false, new[] { 3 })]
    [TestCase(true, new[] { 1, 2, 3 })]
    public async Task AWriteSendsEpisodesByTheUpdateAllSwitch(bool updateAll, int[] expected) {
        using var harness = new SyncHarness();
        harness.Configuration.simklUpdateAll = updateAll;
        var sent = new List<Helpers.SentRequest>();
        var api = new SimklApiCalls(Helpers.RecordedSequence(sent, (HttpStatusCode.OK,
                "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}")),
            new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new Dictionary<string, string> { { "simkl-api-key", "client" } },
            new UserConfig { UserApiAuth = [new UserApiAuth { Name = ApiName.Simkl, AccessToken = String.Empty }] });

        Assert.That(await api.UpdateAnime(45006, SimklStatus.watching, true, new ArmClient.OfflineDatabaseResponse(), 3), Is.True);

        using var body = JsonDocument.Parse(sent[0].Body!);
        var episodes = body.RootElement.GetProperty("shows")[0].GetProperty("episodes").EnumerateArray().Select(episode => episode.GetProperty("number").GetInt32());
        Assert.That(episodes, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = responseCode, ResponseContent = body },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new ArmClient.OfflineDatabaseResponse(), 1), Is.False);
    }

    [TestCase(0, "[]", false)]
    [TestCase(1, "[]", true)]
    [TestCase(1, "[{\"ids\":{\"simkl\":45006}}]", false)]
    public async Task HistoryAcknowledgementRequiresAnActualAdditionAndNoUnresolvedTitle(int episodes, string notFoundShows, bool expected) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":" + episodes + ",\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":" + notFoundShows + ",\"movies\":[],\"episodes\":[]}}" }
        });
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new ArmClient.OfflineDatabaseResponse(), 1), Is.EqualTo(expected));
    }

    [TestCase(45006, true)]
    [TestCase(99999, false)]
    public async Task HistoryReceiptMustAcknowledgeSubmittedTitle(int acknowledgedId, bool expected) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":" + acknowledgedId + "}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}" }
        });
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new ArmClient.OfflineDatabaseResponse(), 1), Is.EqualTo(expected));
    }

    private const string HistoryAdded = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}";

    private static jellyfin_anidoki.Trackers.Simkl.SimklTrackerClient RatingClient(List<Helpers.SentRequest> sent, params (HttpStatusCode Code, string Body)[] responses) =>
        new(new SimklApiCalls(Helpers.RecordedSequence(sent, responses),
            new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new Dictionary<string, string> { { "simkl-api-key", "client" } },
            new UserConfig { UserApiAuth = [new UserApiAuth { Name = ApiName.Simkl, AccessToken = String.Empty }] }));

    private static readonly ArmClient.OfflineDatabaseResponse FixtureIds = new() { MyAnimeList = 1 };

    [TestCase(8, 80)]
    [TestCase(null, null)]
    public void TheRatingIsReadFromTheUserList(int? rating, int? raw) {
        var entry = JsonSerializer.Deserialize<SimklUserEntry>(rating == null ? "{\"status\":\"watching\"}" : $"{{\"status\":\"watching\",\"user_rating\":{rating}}}")!;

        var anime = jellyfin_anidoki.Trackers.ClassConversions.ConvertSimklAnime(new SimklExtendedMedia { Title = "Show" }, entry);

        Assert.That(anime.MyListStatus!.ScoreRaw, Is.EqualTo(raw));
    }

    [Test]
    public async Task AScoreIsASecondCallAfterTheListWrite() {
        using var harness = new SyncHarness();
        var sent = new List<Helpers.SentRequest>();
        var client = RatingClient(sent, (HttpStatusCode.OK, HistoryAdded), (HttpStatusCode.Created, "{}"));

        var response = await client.UpdateAnime(45006, 3, Status.Watching, ids: FixtureIds, isShow: true, scoreRaw: 85);

        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(sent[0].Uri!.AbsolutePath, Does.EndWith("/sync/history"));
        Assert.That(sent[1].Uri!.AbsolutePath, Does.EndWith("/sync/ratings"));
        using var body = JsonDocument.Parse(sent[1].Body!);
        Assert.That(body.RootElement.GetProperty("shows")[0].GetProperty("rating").GetInt32(), Is.EqualTo(9));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(90));
    }

    [Test]
    public async Task ClearingCallsRatingsRemove() {
        using var harness = new SyncHarness();
        var sent = new List<Helpers.SentRequest>();
        var client = RatingClient(sent, (HttpStatusCode.OK, HistoryAdded), (HttpStatusCode.OK, "{}"));

        var response = await client.UpdateAnime(45006, 3, Status.Watching, ids: FixtureIds, isShow: true, clearScore: true);

        Assert.That(sent[1].Uri!.AbsolutePath, Does.EndWith("/sync/ratings/remove"));
        Assert.That(sent[1].Body, Does.Not.Contain("rating"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(0));
    }

    [Test]
    public async Task AFailedRatingCallKeepsTheListWrite() {
        using var harness = new SyncHarness();
        var sent = new List<Helpers.SentRequest>();
        var client = RatingClient(sent, (HttpStatusCode.OK, HistoryAdded), (HttpStatusCode.InternalServerError, ""));

        var response = await client.UpdateAnime(45006, 3, Status.Watching, ids: FixtureIds, isShow: true, scoreRaw: 85);

        Assert.That(response!.AcknowledgedProgress, Is.EqualTo(3));
        Assert.That(response.AcknowledgedScoreRaw, Is.Null);

        var adapter = new TrackerProvider(ApiName.Simkl, RatingClient([], (HttpStatusCode.OK, HistoryAdded), (HttpStatusCode.InternalServerError, "")), TimeProvider.System);
        var entry = new EntryRef(ApiName.Simkl, 45006, null, new CrossReference(new ResolvedIds(null, 1, null, null), "Show", MediaKind.Series));
        var receipt = await adapter.Apply(new EntryState(entry, 12, new ListState(TrackerStatus.Watching, 2, false, null), DateTimeOffset.UnixEpoch),
            new EntryChange(3, TrackerStatus.Watching, Score: 85), System.Threading.CancellationToken.None);
        Assert.That(receipt!.Complete, Is.False);
        Assert.That(receipt.Progress, Is.EqualTo(3));
    }

    [Test]
    public async Task AWriteWithoutAScoreMakesOneCall() {
        using var harness = new SyncHarness();
        var sent = new List<Helpers.SentRequest>();
        var client = RatingClient(sent, (HttpStatusCode.OK, HistoryAdded), (HttpStatusCode.OK, "{}"));

        var response = await client.UpdateAnime(45006, 3, Status.Watching, ids: FixtureIds, isShow: true);

        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That(response!.AcknowledgedScoreRaw, Is.Null);
    }
}
