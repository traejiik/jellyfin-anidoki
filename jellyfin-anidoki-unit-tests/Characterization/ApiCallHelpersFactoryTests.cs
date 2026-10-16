using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
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

namespace jellyfin_anidoki_unit_tests.Characterization;

[TestFixture, NonParallelizable]
public class ApiCallHelpersFactoryTests {
    private string _directory = null!;
    private Plugin _plugin = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-seam-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task UpdateUsesTheInjectedProviderClient() {
        _plugin.Configuration.UserConfig = [
            new UserConfig {
                UserId = _user,
                LibraryToCheck = Array.Empty<string>(),
                UserApiAuth = [new UserApiAuth { Name = ApiName.AniList, AccessToken = "accessToken", RefreshToken = "refreshToken" }]
            }
        ];
        var fake = new Mock<IApiCallHelpers>();
        fake.Setup(f => f.SearchAnime("Seam Movie")).ReturnsAsync(new List<Anime>());
        var requested = new List<ApiName>();
        var updater = new UpdateProviderStatus(new Mock<ILibraryManager>().Object, new NullLoggerFactory(),
            new Mock<IHttpContextAccessor>().Object, new Mock<IServerApplicationHost>().Object, new Mock<IHttpClientFactory>().Object,
            new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object) {
            ApiCallHelpersFactory = (provider, _) => {
                requested.Add(provider);
                return fake.Object;
            }
        };

        await updater.UpdateInsideGate(new Movie { Id = Guid.NewGuid(), Name = "Seam Movie", ProviderIds = new Dictionary<string, string>() },
            _user, true, null, SyncUpdateSource.Playback);

        Assert.That(requested, Is.EqualTo(new[] { ApiName.AniList }));
        fake.Verify(f => f.SearchAnime("Seam Movie"), Times.Once);
    }
}
