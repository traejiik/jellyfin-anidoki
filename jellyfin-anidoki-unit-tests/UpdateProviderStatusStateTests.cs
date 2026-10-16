using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests;

/// <summary>
/// Drives <see cref="UpdateProviderStatus.UpdateInsideGate"/> end to end against fixed HTTP fixtures.
/// Movies are used because they need no Season/Series wiring to reach the ID-resolution and provider loop.
/// </summary>
[TestFixture, NonParallelizable]
public class UpdateProviderStatusStateTests {
    private const int ResolvedAniListId = 101;
    private const int ResolvedMalId = 201;

    private string _directory = null!;
    private Plugin _plugin = null!;
    private readonly Guid _user = Guid.NewGuid();
    private UpdateProviderStatus _updateProviderStatus = null!;

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-update-state-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);

        IHttpClientFactory httpClientFactory = null!;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/ids"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                    Anilist = ResolvedAniListId,
                    MyAnimeList = ResolvedMalId
                })
            },
            new() {
                RequestMethod = HttpMethod.Post,
                RequestUrlMatch = url => url.Contains("anilist"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AniListSearch.AniListSearchMedia {
                    Data = new AniListSearch.AniListSearchData {
                        Page = new AniListSearch.Page {
                            Media = new List<AniListSearch.Media>(),
                            PageInfo = new AniListSearch.PageInfo { HasNextPage = false }
                        }
                    }
                })
            }
        }, ref httpClientFactory);

        _updateProviderStatus = new UpdateProviderStatus(new Mock<ILibraryManager>().Object, new NullLoggerFactory(),
            new Mock<IHttpContextAccessor>().Object, new Mock<IServerApplicationHost>().Object, httpClientFactory,
            new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private void ConfigureUser(params ApiName[] providers) {
        _plugin.Configuration.UserConfig = [
            new UserConfig {
                UserId = _user,
                LibraryToCheck = Array.Empty<string>(),
                UserApiAuth = providers.Select(provider => new UserApiAuth {
                    Name = provider,
                    AccessToken = "accessToken",
                    RefreshToken = "refreshToken"
                }).ToArray()
            }
        ];
    }

    private static Movie MovieWithIds(string name, Dictionary<string, string> providerIds) =>
        new() { Id = Guid.NewGuid(), Name = name, ProviderIds = providerIds };

    /// <summary>
    /// SyncProviderFromLocal reuses one instance across series. An item that resolves no IDs must not inherit the
    /// previous item's IDs, or its progress is written to the previous item's tracker entry.
    /// </summary>
    [Test]
    public async Task SecondUpdateWithoutIdsDoesNotReuseFirstUpdatesIds() {
        ConfigureUser();

        await _updateProviderStatus.UpdateInsideGate(
            MovieWithIds("First", new Dictionary<string, string> { ["AniList"] = ResolvedAniListId.ToString() }),
            _user, true, null, SyncUpdateSource.Playback);
        Assert.That(_updateProviderStatus.ResolvedIds.MyAnimeList, Is.EqualTo(ResolvedMalId), "first update should resolve IDs");

        await _updateProviderStatus.UpdateInsideGate(
            MovieWithIds("Second", new Dictionary<string, string>()),
            _user, true, null, SyncUpdateSource.Playback);

        Assert.That(_updateProviderStatus.ResolvedIds.Anilist, Is.Null);
        Assert.That(_updateProviderStatus.ResolvedIds.MyAnimeList, Is.Null);
        Assert.That(_updateProviderStatus.ResolvedIds.Kitsu, Is.Null);
        Assert.That(_updateProviderStatus.ResolvedIds.AniDb, Is.Null);
    }
}
