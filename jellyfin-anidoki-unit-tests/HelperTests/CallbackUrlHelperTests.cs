using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using jellyfin_anidoki;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture, NonParallelizable]
public class CallbackUrlHelperTests
{
    private string _directory = null!;
    private Plugin _plugin = null!;
    private Mock<IApplicationPaths> _paths = null!;
    private Mock<IServerApplicationHost> _serverHost = null!;
    private Mock<IHttpClientFactory> _httpClientFactory = null!;
    private DefaultHttpContext _context = null!;
    private MemoryCache _cache = null!;
    private AniDokiController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-callback-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _paths = new Mock<IApplicationPaths>();
        _paths.SetupGet(paths => paths.PluginsPath).Returns(_directory);
        _paths.SetupGet(paths => paths.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(serializer => serializer.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(_paths.Object, new Mock<IServerConfigurationManager>().Object,
            xml.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.authenticationLinkExpireTimeMinutes = 1440;
        _plugin.Configuration.callbackUrl = "https://saved.example/jellyfin";
        _serverHost = new Mock<IServerApplicationHost>();
        _serverHost.SetupGet(host => host.HttpPort).Returns(8096);
        _serverHost.SetupGet(host => host.HttpsPort).Returns(8920);
        _httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        _context = new DefaultHttpContext();
        _context.Connection.LocalIpAddress = IPAddress.Loopback;
        _cache = new MemoryCache(new MemoryCacheOptions());
        _controller = new AniDokiController(_httpClientFactory.Object, NullLoggerFactory.Instance,
            _serverHost.Object, new HttpContextAccessor { HttpContext = _context },
            new Mock<ILibraryManager>().Object, new Mock<IUserManager>().Object,
            _paths.Object, new Mock<IUserDataManager>().Object, _cache) {
            ControllerContext = new ControllerContext { HttpContext = _context }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        Directory.Delete(_directory, true);
    }

    [TestCase("http://host:8096", "http://host:8096/AniDoki/authCallback")]
    [TestCase("https://host/jellyfin", "https://host/jellyfin/AniDoki/authCallback")]
    [TestCase("https://host/jellyfin/", "https://host/jellyfin/AniDoki/authCallback")]
    [TestCase("http://[::1]:8096", "http://[::1]:8096/AniDoki/authCallback")]
    [TestCase("https://host/a%20b", "https://host/a%20b/AniDoki/authCallback")]
    public void AuthenticationBuildsExpectedCallback(string address, string expected)
    {
        Assert.Multiple(() => {
            Assert.That(CallbackUrlHelper.Build(address), Is.EqualTo(expected));
            Assert.That(AuthenticationCallback(address), Is.EqualTo(expected));
        });
    }

    [TestCase("file:///tmp/server")]
    [TestCase("https://host?secret=value")]
    [TestCase("https://host/#fragment")]
    [TestCase("https://user:pass@host")]
    [TestCase("/jellyfin")]
    [TestCase("host:8096")]
    [TestCase("https://")]
    [TestCase("https:/host")]
    [TestCase("https://host:invalid")]
    [TestCase("https://host\\jellyfin")]
    public void AuthenticationRejectsInvalidCallbackBases(string address)
    {
        Assert.That(() => CallbackUrlHelper.Build(address), Throws.ArgumentException);
        Assert.That(() => AuthenticationCallback(address), Throws.ArgumentException);
        Assert.That(_plugin.Configuration.callbackUrl, Is.EqualTo("https://saved.example/jellyfin"));
    }

    [Test]
    public void AuthenticationExplicitOverrideWinsOverSavedAddress()
    {
        Assert.That(AuthenticationCallback("https://override.example/server"),
            Is.EqualTo("https://override.example/server/AniDoki/authCallback"));
    }

    [Test]
    public void AuthenticationWithoutOverrideUsesSavedAddress()
    {
        Assert.That(AuthenticationCallback(null),
            Is.EqualTo("https://saved.example/jellyfin/AniDoki/authCallback"));
    }

    [Test]
    public void AuthenticationLocalOverrideUsesConnectionAddressAndPort()
    {
        Assert.That(AuthenticationCallback("local"),
            Is.EqualTo("http://127.0.0.1:8096/AniDoki/authCallback"));
    }

    [Test]
    public void AuthenticationLocalOverrideUsesHttpsPortWhenEnabled()
    {
        _serverHost.SetupGet(host => host.ListenWithHttps).Returns(true);
        Assert.That(AuthenticationCallback("local"),
            Is.EqualTo("https://127.0.0.1:8920/AniDoki/authCallback"));
    }

    [Test]
    public void AuthenticationLocalOverrideBracketsIpv6Address()
    {
        _context.Connection.LocalIpAddress = IPAddress.IPv6Loopback;
        Assert.That(AuthenticationCallback("local"),
            Is.EqualTo("http://[::1]:8096/AniDoki/authCallback"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void AuthenticationWithoutSavedAddressUsesLocalFallback(string? address)
    {
        _plugin.Configuration.callbackUrl = address!;
        Assert.That(AuthenticationCallback(null),
            Is.EqualTo("http://127.0.0.1:8096/AniDoki/authCallback"));
    }

    [Test]
    public void AuthenticationLocalAddressRetainsHostSuppliedPathBase()
    {
        _context.Request.PathBase = "/jellyfin";
        Assert.That(AuthenticationCallback("local"),
            Is.EqualTo("http://127.0.0.1:8096/jellyfin/AniDoki/authCallback"));
    }

    [TestCase("http://host:8096")]
    [TestCase("https://host/jellyfin")]
    [TestCase("https://host/jellyfin/")]
    [TestCase("http://[::1]:8096")]
    public void PreviewMatchesActualAuthenticationCallbackWithoutExternalRequests(string address)
    {
        var result = (OkObjectResult)Preview(address);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.That(json.GetProperty("callbackUrl").GetString(), Is.EqualTo(AuthenticationCallback(address)));
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void BlankPreviewReturnsDetectedLocalBaseAndCallback(string? address)
    {
        var result = (OkObjectResult)Preview(address);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Multiple(() => {
            Assert.That(json.GetProperty("baseAddress").GetString(), Is.EqualTo("http://127.0.0.1:8096"));
            Assert.That(json.GetProperty("callbackUrl").GetString(), Is.EqualTo(AuthenticationCallback("local")));
        });
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [Test]
    public void LocalPreviewAndAuthenticationAgreeForIpv6WithHostSuppliedPathBase()
    {
        _context.Connection.LocalIpAddress = IPAddress.IPv6Loopback;
        _context.Request.PathBase = "/jellyfin";
        var result = (OkObjectResult)Preview(null);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Multiple(() => {
            Assert.That(json.GetProperty("baseAddress").GetString(), Is.EqualTo("http://[::1]:8096/jellyfin"));
            Assert.That(json.GetProperty("callbackUrl").GetString(), Is.EqualTo(AuthenticationCallback("local")));
        });
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [TestCase("file:///tmp/server")]
    [TestCase("https://host?secret=value")]
    [TestCase("https://host/#fragment")]
    [TestCase("https://user:pass@host")]
    public void InvalidPreviewReturnsBadRequestAndPreservesSavedAddress(string address)
    {
        Assert.That(Preview(address), Is.TypeOf<BadRequestObjectResult>());
        Assert.That(_plugin.Configuration.callbackUrl, Is.EqualTo("https://saved.example/jellyfin"));
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [Test]
    public void InvalidSavedAddressRemainsAvailableForCorrection()
    {
        const string invalidAddress = "https://host?unexpected=value";
        _plugin.Configuration.callbackUrl = invalidAddress;
        Assert.That(() => AuthenticationCallback(null), Throws.ArgumentException);
        Assert.That(_plugin.Configuration.callbackUrl, Is.EqualTo(invalidAddress));
    }

    private IActionResult Preview(string? address) => _controller.CallbackPreview(address);

    private string AuthenticationCallback(string? address)
    {
        var authentication = new ApiAuthentication(ApiName.Mal, _httpClientFactory.Object,
            _serverHost.Object, new HttpContextAccessor { HttpContext = _context },
            NullLoggerFactory.Instance, _cache, new Mock<IAsyncDelayer>().Object,
            new ProviderApiAuth { ClientId = "fixture-client", ClientSecret = "fixture-secret" }, address);
        var url = new Uri(authentication.BuildAuthorizeRequestUrl(Guid.NewGuid()));
        return QueryHelpers.ParseQuery(url.Query)["redirect_uri"].ToString();
    }
}
