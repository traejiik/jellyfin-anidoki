using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.Api.Shikimori;
using jellyfin_anidoki.Api.Simkl;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Simkl;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class Simkl {
    private SimklApiCalls _simklApiCalls;
    private IHttpClientFactory _httpClientFactory;

    public void Setup(List<Helpers.HttpCall> httpCalls) {
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
        var mockLoggerFactory = new NullLoggerFactory();
        var mockServerApplicationHost = new Mock<IServerApplicationHost>();
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        _simklApiCalls = new SimklApiCalls(_httpClientFactory, mockLoggerFactory, mockServerApplicationHost.Object, mockHttpContextAccessor.Object, memoryCache, mockDelayer.Object, new Dictionary<string, string> { { "simkl-api-key", String.Empty } }, new UserConfig {
            UserApiAuth = new []{new UserApiAuth {
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
        Assert.IsNotEmpty(result);
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
        Assert.IsNotEmpty(result);
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
        
        var result = await _simklApiCalls.GetAnimeByIdLookup(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
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
        
        var result = await _simklApiCalls.UpdateAnime(45006, SimklStatus.plantowatch, true, new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
            AniDb = 6327
        }, 10);

        Assert.IsTrue(result);
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
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), 1), Is.False);
    }

    [TestCase(0, "[]", false)]
    [TestCase(1, "[]", true)]
    [TestCase(1, "[{\"ids\":{\"simkl\":45006}}]", false)]
    public async Task HistoryAcknowledgementRequiresAnActualAdditionAndNoUnresolvedTitle(int episodes, string notFoundShows, bool expected) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":" + episodes + ",\"statuses\":[{\"request\":{\"ids\":{\"simkl\":45006}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":" + notFoundShows + ",\"movies\":[],\"episodes\":[]}}" }
        });
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), 1), Is.EqualTo(expected));
    }

    [TestCase(45006, true)]
    [TestCase(99999, false)]
    public async Task HistoryReceiptMustAcknowledgeSubmittedTitle(int acknowledgedId, bool expected) {
        Setup(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK,
                ResponseContent = "{\"added\":{\"shows\":0,\"movies\":0,\"episodes\":1,\"statuses\":[{\"request\":{\"ids\":{\"simkl\":" + acknowledgedId + "}},\"response\":{\"status\":\"watching\"}}]},\"not_found\":{\"shows\":[],\"movies\":[],\"episodes\":[]}}" }
        });
        Assert.That(await _simklApiCalls.UpdateAnime(45006, SimklStatus.watching, true, new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), 1), Is.EqualTo(expected));
    }
}