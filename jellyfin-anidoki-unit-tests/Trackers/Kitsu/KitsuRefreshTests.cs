using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers.Kitsu;

/// <summary>
/// Kitsu is linked with the user's own sign-in, so the admin never sets up a Kitsu client. A refresh used to look for one
/// anyway and fail, which unlinked every Kitsu user once their first token expired.
/// </summary>
[TestFixture, NonParallelizable]
public class KitsuRefreshTests {
    private string _directory = null!;
    private Guid _user;

    private sealed class Handler : HttpMessageHandler {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\"}")
            };
        }
    }

    [SetUp]
    public void Setup() {
        _directory = Path.Combine(Path.GetTempPath(), "kitsu-refresh-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        _user = Guid.NewGuid();
        // No Kitsu entry among the provider clients: none is ever set up.
        var configuration = new PluginConfiguration {
            ProviderApiAuth = [],
            UserConfig = [new UserConfig {
                UserId = _user,
                UserApiAuth = [new UserApiAuth { Name = ApiName.Kitsu, AccessToken = "old-access", RefreshToken = "old-refresh" }]
            }]
        };
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(configuration);
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public async Task ARefreshNeedsNoKitsuClient() {
        var handler = new Handler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var authentication = new ApiAuthentication(ApiName.Kitsu, factory.Object, new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpContextAccessor>().Object, new NullLoggerFactory(), new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object);
        UserApiAuth? refreshed = await authentication.GetToken(_user, refreshToken: "old-refresh");

        Assert.That(refreshed?.AccessToken, Is.EqualTo("new-access"));
        Assert.That(handler.Bodies, Is.EqualTo(new[] { "grant_type=refresh_token&refresh_token=old-refresh" }));
    }
}
