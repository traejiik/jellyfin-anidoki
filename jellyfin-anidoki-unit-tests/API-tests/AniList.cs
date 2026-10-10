using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Api.Anilist;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
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
    private AniListApiCalls _aniListApiCalls;
    private ILoggerFactory _loggerFactory;
    private Mock<IServerApplicationHost> _serverApplicationHost;
    private Mock<IHttpContextAccessor> _httpContextAccessor;
    private IHttpClientFactory _httpClientFactory;

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        _loggerFactory = new NullLoggerFactory();
        _serverApplicationHost = new Mock<IServerApplicationHost>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        _aniListApiCalls = new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, memoryCache, mockDelayer.Object, new UserConfig {
            UserApiAuth = new [] {
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

        Assert.IsNotNull(result[0].Id);
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

        Assert.IsTrue(result.Count == 10);
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

        Assert.IsNotNull(result.Id);
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
        var updater = new UpdateProviderStatus(new Mock<ILibraryManager>().Object, _loggerFactory,
            _httpContextAccessor.Object, _serverApplicationHost.Object, _httpClientFactory,
            new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object) {
            ApiName = ApiName.AniList, ApiCallHelpers = new ApiCallHelpers(aniListApiCalls: _aniListApiCalls)
        };
        var collector = new PlaybackOutcomeCollector();
        await updater.UpdateAnimeStatus(new Anime { Id = 1, Title = "Rewatch", NumEpisodes = 12,
            MyListStatus = new MyListStatus { NumEpisodesWatched = 12, Status = Status.Completed } },
            1, setRewatching: true, collector: collector);

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
        var result = await new ApiCallHelpers(aniListApiCalls: _aniListApiCalls).UpdateAnime(1, 5, Status.Completed,
            isRewatching: true, numberOfTimesRewatched: 2);
        Assert.That(result.AcknowledgedProgress, Is.EqualTo(4));
        Assert.That(result.AcknowledgedStatus, Is.EqualTo(Status.Watching));
        Assert.That(result.AcknowledgedRewatching, Is.False);
        Assert.That(result.AcknowledgedRewatchCount, Is.EqualTo(1));
    }
}
