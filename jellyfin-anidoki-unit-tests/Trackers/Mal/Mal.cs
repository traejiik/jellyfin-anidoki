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
using jellyfin_anidoki.Trackers.Simkl;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers.Mal;

public class Mal {
    private MalApiCalls _malApiCalls = null!;
    private ILoggerFactory _loggerFactory = null!;
    private Mock<IServerApplicationHost> _serverApplicationHost = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        _loggerFactory = new NullLoggerFactory();
        _serverApplicationHost = new Mock<IServerApplicationHost>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        _malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, memoryCache, mockDelayer.Object, new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.Mal,
                    RefreshToken = "refreshToken"
                }
            }
        });
    }

    [Test]
    public async Task TestGenericGetUserInformation() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/users/@me"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new MalApiCalls.User {
                    Id = 1,
                    Name = "name",
                    Location = "location",
                    JoinedAt = DateTime.UtcNow,
                    Picture = "picture"
                })
            }
        });
        var result = await _malApiCalls.GetUserInformation();
        Assert.IsNotNull(result!.Id);
    }

    [Test]
    public async Task TestGenericSearchAnime() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/anime"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new SearchAnimeResponse {
                    Data = new List<AnimeList> {
                        new()  {
                            Anime = new Anime {
                                Id = 1
                            }
                        }
                    }
                })
            }
        });

        var result = await _malApiCalls.SearchAnime(String.Empty, new[] { String.Empty });
        Assert.IsTrue(result is { Count: > 0 });
    }

    [Test]
    public async Task TestGenericGetAnime() {
        int animeId = 1;
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith($"/anime/{animeId}"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new Anime {
                    Id = animeId
                })
            }
        });

        var result = await _malApiCalls.GetAnime(animeId);
        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestUpdateAnimeStatus() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Put,
                RequestUrlMatch = url => url.EndsWith("my_list_status"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"status\":\"completed\",\"num_episodes_watched\":1,\"num_times_rewatched\":1}"
            }
        });

        var makeChange = await _malApiCalls.UpdateAnimeStatus(339,
            1,
            Status.Completed,
            isRewatching: true,
            numberOfTimesRewatched: 1,
            startDate: DateTime.UtcNow - TimeSpan.FromHours(1),
            endDate: DateTime.UtcNow);
        Assert.IsNotNull(makeChange);
    }

    /*[Test]
    [TestCase("The Melancholy of Haruhi Suzumiya", "TheMelancholyofHaruhiSuzumiya")]
    [TestCase("Kono Subarashii Sekai ni Shukufuku wo! 2: Kono Subarashii Geijutsu ni Shukufuku wo! ", "KonoSubarashiiSekainiShukufukuwo!2:KonoSubarashiiGeijutsuniShuku")]
    public void TruncateStringAndRemoveSpaces(string input, string expected) =>
        Assert.IsTrue(MalHelper.TruncateQuery(input) == expected);*/

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"error\":\"rejected\"}")]
    [TestCase("{\"status\":\"watching\",\"num_episodes_watched\":1}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Put, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _malApiCalls.UpdateAnimeStatus(1, 1, Status.Watching), Is.Null);
    }

    private static MalApiCalls SignedInApi(IHttpClientFactory factory) =>
        new(factory, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = [new UserApiAuth { AccessToken = "accessToken", Name = ApiName.Mal, RefreshToken = "refreshToken" }]
            });

    private const string EntryWithScore = "{\"id\":500,\"title\":\"Fixture Show\",\"num_episodes\":12,\"my_list_status\":{\"status\":\"watching\",\"score\":SCORE,\"num_episodes_watched\":3,\"is_rewatching\":false,\"updated_at\":\"2026-01-01T00:00:00Z\"}}";

    private const string SavedStatus = "{\"status\":\"watching\",\"score\":SCORE,\"num_episodes_watched\":5,\"is_rewatching\":false,\"updated_at\":\"2026-01-01T00:00:00Z\"}";

    [TestCase("7", 70)]
    [TestCase("0", null)]
    public async Task TheScoreIsReadAsTenPoint(string score, int? raw) {
        var client = new jellyfin_anidoki.Trackers.Mal.MalTrackerClient(SignedInApi(Helpers.Sequence((HttpStatusCode.OK, EntryWithScore.Replace("SCORE", score)))));

        var anime = await client.GetAnime(500);

        Assert.That(anime!.MyListStatus!.ScoreRaw, Is.EqualTo(raw));
    }

    [Test]
    public async Task AScoreIsSentAsAnInteger() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Mal.MalTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedStatus.Replace("SCORE", "9")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching, scoreRaw: 85);

        Assert.That(sent.Single().Body, Does.Contain("score=9"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(90));
    }

    [Test]
    public async Task ClearingSendsZero() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Mal.MalTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedStatus.Replace("SCORE", "0")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching, clearScore: true);

        Assert.That(sent.Single().Body, Does.Contain("score=0"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(0));
    }

    [Test]
    public async Task AWriteWithoutAScoreHasNoScoreField() {
        var sent = new List<Helpers.SentRequest>();
        var client = new jellyfin_anidoki.Trackers.Mal.MalTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedStatus.Replace("SCORE", "7")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching);

        Assert.That(sent.Single().Body, Does.Not.Contain("score"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.Null);
    }
}
