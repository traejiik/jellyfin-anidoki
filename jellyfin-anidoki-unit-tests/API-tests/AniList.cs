using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.AniList;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class AniList {
    private AniListApiCalls _aniListApiCalls = null!;
    private ILoggerFactory _loggerFactory = null!;
    private Mock<IServerApplicationHost> _serverApplicationHost = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private Mock<IAsyncDelayer> _delayer = null!;

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        _loggerFactory = new NullLoggerFactory();
        _serverApplicationHost = new Mock<IServerApplicationHost>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        _delayer = new Mock<IAsyncDelayer>();
        _aniListApiCalls = new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, memoryCache, _delayer.Object, new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.AniList,
                    RefreshToken = "refreshToken"
                }
            }
        });
    }

    [Test]
    public async Task TestGenericSearch() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AniListSearch.AniListSearchMedia {
                    Data = new AniListSearch.AniListSearchData {
                        Page = new AniListSearch.Page {
                            Media = new List<AniListSearch.Media> {
                                new()  {
                                    Id = 1
                                }
                            },
                            PageInfo = new AniListSearch.PageInfo {
                                HasNextPage = false
                            }
                        }
                    }
                })
            }
        });

        var result = await _aniListApiCalls.SearchAnime(String.Empty);

        Assert.IsNotNull(result![0].Id);
    }

    [Test]
    public async Task TestGettingCurrentUser() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AniListViewer.AniListGetViewer {
                    Data = new AniListViewer.AniListViewerData {
                        Viewer = new AniListViewer.Viewer {
                            Id = 1
                        }
                    }
                })
            }
        });
        var result = await _aniListApiCalls.GetCurrentUser();

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestUpdatingAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"SaveMediaListEntry\":{\"id\":123,\"progress\":1,\"status\":\"CURRENT\",\"repeat\":1}}}"
            }
        });
        var result = await _aniListApiCalls.UpdateAnime(1,
            AniListSearch.MediaListStatus.Current,
            1,
            numberOfTimesRewatched: 1,
            startDate: DateTime.UtcNow - TimeSpan.FromHours(1),
            endDate: DateTime.UtcNow);

        Assert.IsTrue(result);
    }

    [Test]
    public async Task TestGenericSearchPaging() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AniListSearch.AniListSearchMedia {
                    Data = new AniListSearch.AniListSearchData {
                        Page = new AniListSearch.Page {
                            Media = new List<AniListSearch.Media> {
                                new()  {
                                    Id = 1
                                }
                            },
                            PageInfo = new AniListSearch.PageInfo {
                                HasNextPage = true
                            }
                        }
                    }
                })
            }
        });
        var result = await _aniListApiCalls.SearchAnime(String.Empty);

        Assert.IsTrue(result!.Count == 10);
        // Pages 2 to 10 each report another page, so the search waits nine times between requests.
        _delayer.Verify(delayer => delayer.Delay(TimeSpan.FromSeconds(1)), Times.Exactly(9));
    }

    [Test]
    public async Task TestGetAnime() {
        Setup(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AniListGet.AniListGetMedia {
                    Data = new AniListGet.AniListGetData {
                        Media = new AniListSearch.Media {
                            Id = 1
                        }
                    }
                })
            }
        });
        var result = await _aniListApiCalls.GetAnime(1);

        Assert.IsNotNull(result!.Id);
    }

    [Test]
    public async Task RelatedAnimeCarriesTheCoverFromTheExistingRead() {
        Setup([new Helpers.HttpCall {
            RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
            ResponseContent = """{"data":{"Media":{"id":1,"relations":{"edges":[{"relationType":"SEQUEL","node":{"id":2,"type":"ANIME","title":{"english":"Next"},"coverImage":{"medium":"https://example.test/anilist.jpg"}}}]}}}}"""
        }]);
        var tracker = new TrackerProvider(ApiName.AniList, new AniListTrackerClient(_aniListApiCalls), TimeProvider.System);

        var relations = await tracker.GetRelations(new EntryRef(ApiName.AniList, 1, null), default);

        Assert.That(relations!.Single().CoverUrl, Is.EqualTo("https://example.test/anilist.jpg"));
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"data\":{\"SaveMediaListEntry\":{\"id\":123}}}")]
    [TestCase("{\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"data\":{\"SaveMediaListEntry\":null}}")]
    [TestCase("{\"data\":{\"SaveMediaListEntry\":{\"id\":1,\"clientMutationId\":null}},\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"data\":{\"SaveMediaListEntry\":{\"id\":123}}}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = responseCode, ResponseContent = body },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _aniListApiCalls.UpdateAnime(1, AniListSearch.MediaListStatus.Current, 1), Is.False);
    }

    [TestCase(0)]
    [TestCase(null)]
    public async Task RewatchFirstReceiptAndFailedSecondWriteOnlyConfirmReturnedFields(int? returnedProgress) {
        int requests = 0;
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, RequestUrlMatch = _ => ++requests == 1,
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"SaveMediaListEntry\":{\"id\":123,\"status\":\"REPEATING\",\"progress\":" +
                    (returnedProgress?.ToString() ?? "null") + "}}}" },
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"SaveMediaListEntry\":null},\"errors\":[{\"message\":\"rejected\"}]}" }
        });
        var tracker = new TrackerProvider(ApiName.AniList, new AniListTrackerClient(_aniListApiCalls), TimeProvider.System);
        var entry = new EntryRef(ApiName.AniList, 1, null);
        var state = new EntryState(entry, 12, new ListState(TrackerStatus.Completed, 12, false, 0), DateTimeOffset.UtcNow,
            new EntryTitles("Rewatch", null, null, []));
        var collector = new PlaybackOutcomeCollector();
        await new Executor(tracker, new PlanRules(false, true), PlanPolicy.Automatic, collector, TimeProvider.System, NullLogger.Instance)
            .Execute(new Resolved(new ResolvedTarget(entry, 1, TargetRole.Played, "Rewatch", state), ResolvedBy.Ids, []), CancellationToken.None);

        Assert.That(requests, Is.EqualTo(2));
        Assert.That(collector.Outcomes, Has.Length.EqualTo(2));
        Assert.That(collector.Outcomes[0].Kind, Is.EqualTo(OutcomeKind.Confirmed));
        Assert.That(collector.Outcomes[0].ConfirmedStatus, Is.EqualTo(Status.Rewatching));
        Assert.That(collector.Outcomes[0].ConfirmedProgress, Is.EqualTo(returnedProgress));
        Assert.That(collector.Outcomes[1].Kind, Is.EqualTo(OutcomeKind.Unconfirmed));
        Assert.That(collector.Outcomes[1].ConfirmedProgress, Is.Null);
    }

    [Test]
    public async Task ReceiptUsesReturnedStatusAndRepeatInsteadOfRequestedValues() {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"SaveMediaListEntry\":{\"id\":123,\"status\":\"CURRENT\",\"progress\":4,\"repeat\":1}}}" }
        });
        var result = await new AniListTrackerClient(_aniListApiCalls).UpdateAnime(1, 5, Status.Completed,
            isRewatching: true, numberOfTimesRewatched: 2);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AcknowledgedProgress, Is.EqualTo(4));
        Assert.That(result.AcknowledgedStatus, Is.EqualTo(Status.Watching));
        Assert.That(result.AcknowledgedRewatching, Is.False);
        Assert.That(result.AcknowledgedRewatchCount, Is.EqualTo(1));
    }

    private AniListApiCalls Api(IHttpClientFactory factory) =>
        new(factory, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig());

    /// <summary>A later page that fails reads like a failed first page, not as a crash.</summary>
    [Test]
    public async Task ASearchWhoseSecondPageFailsReturnsNothing() {
        string firstPage = JsonSerializer.Serialize(new AniListSearch.AniListSearchMedia {
            Data = new AniListSearch.AniListSearchData {
                Page = new AniListSearch.Page { Media = [new AniListSearch.Media { Id = 1 }], PageInfo = new AniListSearch.PageInfo { HasNextPage = true } }
            }
        });

        var result = await Api(Helpers.Sequence((HttpStatusCode.OK, firstPage), (HttpStatusCode.TooManyRequests, ""))).SearchAnime("Fixture");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task AListWhoseSecondChunkFailsReturnsNothing() {
        string firstChunk = JsonSerializer.Serialize(new AniListMediaList.AniListUserMediaList {
            Data = new AniListMediaList.Data {
                MediaListCollection = new AniListMediaList.MediaListCollection {
                    HasNextChunk = true,
                    MediaList = [new AniListMediaList.EntriesContainer { Entries = [new AniListMediaList.Entries { Progress = 1 }] }]
                }
            }
        });

        var result = await Api(Helpers.Sequence((HttpStatusCode.OK, firstChunk), (HttpStatusCode.TooManyRequests, "")))
            .GetAnimeList(1, AniListSearch.MediaListStatus.Completed);

        Assert.That(result, Is.Null);
    }

    /// <summary>A user with no linked AniList account has no viewer to read; that is no user, not a crash.</summary>
    [Test]
    public async Task AUserWithNoViewerReadsAsNoUser() {
        var result = await new AniListTrackerClient(Api(Helpers.Sequence((HttpStatusCode.OK, "{}")))).GetUser();

        Assert.That(result, Is.Null);
    }

    /// <summary>Like <see cref="Api"/>, with a linked AniList account, so authenticated calls are sent.</summary>
    private AniListApiCalls SignedInApi(IHttpClientFactory factory) =>
        new(factory, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = [new UserApiAuth { AccessToken = "accessToken", Name = ApiName.AniList, RefreshToken = "refreshToken" }]
            });

    private static System.Text.Json.JsonElement Body(Helpers.SentRequest request) =>
        System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;

    private static string Query(Helpers.SentRequest request) => Body(request).GetProperty("query").GetString()!;

    private static string? Variable(Helpers.SentRequest request, string name) =>
        Body(request).GetProperty("variables").TryGetProperty(name, out var value) ? value.GetString() : null;

    private const string EntryWithScore = "{\"data\":{\"Media\":{\"id\":500,\"episodes\":12,\"mediaListEntry\":{\"status\":\"CURRENT\",\"progress\":3,\"repeat\":0,\"score\":SCORE}}}}";

    [Test]
    public async Task TheEntryScoreIsReadOnTheHundredPointScale() {
        var sent = new List<Helpers.SentRequest>();
        var client = new AniListTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, EntryWithScore.Replace("SCORE", "85.0")))));

        var anime = await client.GetAnime(500);

        Assert.That(Query(sent.Single()), Does.Contain("score(format: POINT_100)"));
        Assert.That(anime!.MyListStatus!.ScoreRaw, Is.EqualTo(85));
    }

    [Test]
    public async Task AnUnratedEntryReadsAsNull() {
        var client = new AniListTrackerClient(SignedInApi(Helpers.Sequence((HttpStatusCode.OK, EntryWithScore.Replace("SCORE", "0")))));

        var anime = await client.GetAnime(500);

        Assert.That(anime!.MyListStatus!.ScoreRaw, Is.Null);
    }

    private const string SavedEntry = "{\"data\":{\"SaveMediaListEntry\":{\"id\":1,\"progress\":5,\"status\":\"CURRENT\",\"repeat\":0,\"score\":SCORE}}}";

    [Test]
    public async Task AScoreIsWrittenAsScoreRaw() {
        var sent = new List<Helpers.SentRequest>();
        var client = new AniListTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedEntry.Replace("SCORE", "85")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching, scoreRaw: 85);

        string query = Query(sent.Single());
        Assert.That(query, Does.Contain("$scoreRaw: Int"));
        Assert.That(query, Does.Contain("scoreRaw: $scoreRaw"));
        Assert.That(query, Does.Contain("score(format: POINT_100)"));
        Assert.That(Variable(sent.Single(), "scoreRaw"), Is.EqualTo("85"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(85));
    }

    [Test]
    public async Task ClearingWritesScoreRawZero() {
        var sent = new List<Helpers.SentRequest>();
        var client = new AniListTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedEntry.Replace("SCORE", "0")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching, clearScore: true);

        Assert.That(Variable(sent.Single(), "scoreRaw"), Is.EqualTo("0"));
        Assert.That(response!.AcknowledgedScoreRaw, Is.EqualTo(0));
    }

    [Test]
    public async Task AWriteWithoutAScoreIsUnchanged() {
        var sent = new List<Helpers.SentRequest>();
        var client = new AniListTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, SavedEntry.Replace("SCORE", "0")))));

        var response = await client.UpdateAnime(500, 5, Status.Watching);

        Assert.That(Query(sent.Single()), Does.Not.Contain("score"));
        Assert.That(Variable(sent.Single(), "scoreRaw"), Is.Null);
        Assert.That(response!.AcknowledgedScoreRaw, Is.Null);
    }

    [TestCase("POINT_100", ScoreFormat.Point100)]
    [TestCase("POINT_10_DECIMAL", ScoreFormat.Point10Decimal)]
    [TestCase("POINT_10", ScoreFormat.Point10)]
    [TestCase("POINT_5", ScoreFormat.Point5)]
    [TestCase("POINT_3", ScoreFormat.Point3)]
    public async Task TheViewersFormatIsRead(string scoreFormat, ScoreFormat expected) {
        var sent = new List<Helpers.SentRequest>();
        string response = $"{{\"data\":{{\"Viewer\":{{\"id\":1,\"mediaListOptions\":{{\"scoreFormat\":\"{scoreFormat}\"}}}}}}}}";
        var client = new AniListTrackerClient(SignedInApi(Helpers.RecordedSequence(sent, (HttpStatusCode.OK, response))));

        Assert.That(await client.GetScoreFormat(), Is.EqualTo(expected));
        Assert.That(Query(sent.Single()), Does.Contain("mediaListOptions"));
        Assert.That(Query(sent.Single()), Does.Contain("scoreFormat"));
    }

    [Test]
    public async Task AnUnknownFormatReadsAsNull() {
        var client = new AniListTrackerClient(SignedInApi(Helpers.Sequence((HttpStatusCode.OK,
            "{\"data\":{\"Viewer\":{\"id\":1,\"mediaListOptions\":{\"scoreFormat\":\"POINT_7\"}}}}"))));

        Assert.That(await client.GetScoreFormat(), Is.Null);
    }
}
