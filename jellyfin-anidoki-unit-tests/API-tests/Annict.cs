using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Annict;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class Annict {
    private AnnictApiCalls _annictApiCalls = null!;
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
        _annictApiCalls = new AnnictApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost.Object, _httpContextAccessor.Object, memoryCache, mockDelayer.Object, new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.Annict,
                    RefreshToken = "refreshToken"
                }
            }
        });
    }

    [Test]
    public async Task TestGenericSearch() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Post,
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnnictSearch.AnnictSearchMedia {
                    AnnictSearchData = new AnnictSearch.AnnictSearchData {
                        SearchWorks = new AnnictSearch.SearchWorks {
                            Nodes = new List<AnnictSearch.AnnictAnime> {
                                new()  {
                                    Id = String.Empty
                                }
                            }
                        }
                    }
                })
            }
        });

        var result = await _annictApiCalls.SearchAnime(String.Empty);

        Assert.IsNotNull(result?[0]?.Id);
    }

    [Test]
    public async Task TestGettingCurrentUser() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Post,
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnnictViewer.AnnictViewerRoot {
                    AnnictSearchData = new AnnictViewer.AnnictViewerData {
                        Viewer = new AnnictViewer.AnnictViewerDetails {
                            username = String.Empty
                        }
                    }
                })
            }
        });

        var result = await _annictApiCalls.GetCurrentUser();

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestGettingUserAnimeList() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Post,
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnnictMediaList.AnnictUserMediaList {
                    AnnictSearchData = new AnnictMediaList.AnnictUserMediaListData {
                        Viewer = new AnnictMediaList.AnnictUserMediaListViewer {
                            AnnictUserMediaListLibraryEntries = new AnnictMediaList.AnnictUserMediaListLibraryEntries {
                                Nodes = new List<AnnictSearch.AnnictAnime> {
                                    new()  {
                                        Id = String.Empty
                                    }
                                },
                                PageInfo = new AnnictMediaList.PageInfo {
                                    HasNextPage = false
                                }
                            }
                        }
                    }
                })
            }
        });

        var result = await _annictApiCalls.GetAnimeList(AnnictSearch.AnnictMediaStatus.Watched);

        Assert.IsNotNull(result);
    }

    [Test]
    public async Task TestUpdatingAnime() {
        Setup(new List<Helpers.HttpCall> {
            new()  {
                RequestMethod = HttpMethod.Post,
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"data\":{\"updateStatus\":{\"clientMutationId\":null}}}"
            }
        });

        var result = await _annictApiCalls.UpdateAnime("V29yay02Njg=", AnnictSearch.AnnictMediaStatus.Watched);

        Assert.IsTrue(result);
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"data\":{\"updateStatus\":null}}")]
    [TestCase("{\"data\":{\"updateStatus\":{\"id\":1,\"clientMutationId\":null}},\"errors\":[{\"message\":\"rejected\"}]}")]
    [TestCase("{\"data\":{\"updateStatus\":{\"clientMutationId\":null}}}", HttpStatusCode.BadRequest)]
    public async Task UpdateRejectsMissingOrErrorAcknowledgement(string body, HttpStatusCode responseCode = HttpStatusCode.OK) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = responseCode, ResponseContent = body },
            new() { RequestMethod = HttpMethod.Patch, ResponseCode = responseCode, ResponseContent = body }
        });
        Assert.That(await _annictApiCalls.UpdateAnime("work", AnnictSearch.AnnictMediaStatus.Watched), Is.False);
    }

    private static string ListPage(string node, bool hasNextPage, string endCursor) =>
        JsonSerializer.Serialize(new {
            data = new {
                viewer = new {
                    libraryEntries = new {
                        nodes = new[] { new { id = node } },
                        pageInfo = new { hasNextPage, startCursor = $"start-{node}", endCursor }
                    }
                }
            }
        });

    private AnnictApiCalls ListClient(List<Helpers.SentRequest> sent, params (HttpStatusCode, string)[] responses) =>
        new(Helpers.RecordedSequence(sent, responses), new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpContextAccessor>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = [new UserApiAuth { AccessToken = "accessToken", Name = ApiName.Annict, RefreshToken = "refreshToken" }]
            });

    /// <summary>The next page starts after the last entry of this one, so a list longer than a page is read once, not page 1 again.</summary>
    [Test]
    public async Task GetAnimeListFollowsTheEndCursor() {
        var sent = new List<Helpers.SentRequest>();
        var api = ListClient(sent, (HttpStatusCode.OK, ListPage("a", true, "end-a")), (HttpStatusCode.OK, ListPage("b", false, "end-b")));

        var result = await api.GetAnimeList(AnnictSearch.AnnictMediaStatus.Watching);

        Assert.That(sent, Has.Count.EqualTo(2));
        using var second = System.Text.Json.JsonDocument.Parse(sent[1].Body!);
        Assert.That(second.RootElement.GetProperty("variables").GetProperty("after").GetString(), Is.EqualTo("end-a"));
        Assert.That(result?.ConvertAll(node => node.Id), Is.EqualTo(new[] { "a", "b" }));
    }

    /// <summary>A server that keeps saying there is more without moving the cursor fails the list instead of asking forever.</summary>
    [Test]
    public async Task GetAnimeListStopsWhenTheCursorDoesNotMove() {
        var sent = new List<Helpers.SentRequest>();
        var api = ListClient(sent, (HttpStatusCode.OK, ListPage("a", true, "end-a")), (HttpStatusCode.OK, ListPage("a", true, "end-a")), (HttpStatusCode.OK, "{}"));

        var result = await api.GetAnimeList(AnnictSearch.AnnictMediaStatus.Watching);

        Assert.That(result, Is.Null);
        Assert.That(sent, Has.Count.EqualTo(2));
    }

    /// <summary>A list read that got no reply (here, no linked account) returns nothing, as the other reads do.</summary>
    [Test]
    public async Task AListReadThatGetsNoReplyReturnsNothing() {
        var api = new AnnictApiCalls(Helpers.Sequence((HttpStatusCode.OK, "{}")), new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpContextAccessor>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig());

        Assert.That(await api.GetAnimeList(AnnictSearch.AnnictMediaStatus.Watching), Is.Null);
    }
}
