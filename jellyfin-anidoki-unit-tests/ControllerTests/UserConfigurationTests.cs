using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

[TestFixture, NonParallelizable]
public partial class UserConfigurationTests
{
    private string _directory = null!;
    private Plugin _plugin = null!;
    private User _requester = null!;
    private User _target = null!;
    private UserConfig _requesterConfig = null!;
    private UserConfig _targetConfig = null!;
    private Mock<IUserManager> _userManager = null!;
    private Mock<ILibraryManager> _libraryManager = null!;
    private Mock<IApplicationPaths> _paths = null!;
    private Mock<IHttpClientFactory> _httpClientFactory = null!;
    private Mock<IServerApplicationHost> _serverHost = null!;
    private Mock<IXmlSerializer> _xmlSerializer = null!;
    private MemoryCache _memoryCache = null!;
    private HttpClient? _httpClient;
    private AniDokiController _controller = null!;
    private Guid _libraryId;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-controller-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _paths = new Mock<IApplicationPaths>();
        _paths.SetupGet(paths => paths.PluginsPath).Returns(_directory);
        _paths.SetupGet(paths => paths.PluginConfigurationsPath).Returns(_directory);
        _xmlSerializer = new Mock<IXmlSerializer>();
        _xmlSerializer.Setup(serializer => serializer.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(_paths.Object, new Mock<IServerConfigurationManager>().Object,
            _xmlSerializer.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.enableUserPages = true;
        _plugin.Configuration.callbackUrl = "http://localhost:8096";
        _plugin.Configuration.authenticationLinkExpireTimeMinutes = 1440;
        _plugin.Configuration.ProviderApiAuth = [new ProviderApiAuth {
            Name = ApiName.Mal, ClientId = "fixture-client", ClientSecret = "fixture-secret"
        }];

        _requester = new User("requester", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _target = new User("target", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _requesterConfig = CreateConfig(_requester.Id, "requester");
        _targetConfig = CreateConfig(_target.Id, "target");
        _plugin.Configuration.UserConfig = [_requesterConfig, _targetConfig];
        _userManager = new Mock<IUserManager>();
        _userManager.Setup(manager => manager.GetUserById(_requester.Id)).Returns(_requester);
        _userManager.Setup(manager => manager.GetUserById(_target.Id)).Returns(_target);

        _libraryId = Guid.NewGuid();
        SetPermission(_requester, PermissionKind.EnableAllFolders, true);
        SetPermission(_target, PermissionKind.EnableAllFolders, true);
        _libraryManager = new Mock<ILibraryManager>();
        _libraryManager.Setup(manager => manager.GetVirtualFolders()).Returns([
            new VirtualFolderInfo { ItemId = _libraryId.ToString(), Name = "Anime" }
        ]);
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _serverHost = new Mock<IServerApplicationHost>();
        _serverHost.SetupGet(host => host.HttpPort).Returns(8096);
        _serverHost.SetupGet(host => host.HttpsPort).Returns(8920);
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        var context = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimValues.UserId, _requester.Id.ToString())
            ]))
        };
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        _controller = new AniDokiController(_httpClientFactory.Object, NullLoggerFactory.Instance,
            _serverHost.Object, new HttpContextAccessor { HttpContext = context },
            _libraryManager.Object, _userManager.Object, _paths.Object,
            new Mock<IUserDataManager>().Object, _memoryCache) {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        _memoryCache.Dispose();
        Directory.Delete(_directory, true);
    }

    private async Task<IActionResult> InvokeUserOperation(string operation, Guid user) => operation switch {
        "get" => _controller.GetConfigurationUser(user),
        "put" => _controller.UpdateConfigurationUser(user, new UserEditableConfig { LibraryToCheck = [] }),
        "profile" => await _controller.GetUserUser(ApiName.Mal, user),
        "authorize" => _controller.BuildAuthorizeRequestUrlUser(ApiName.Mal, user),
        "grant" => await _controller.PasswordGrantAuthenticationUser(ApiName.Kitsu, user, "fixture-user", "fixture-password"),
        "deauthenticate" => _controller.DeauthenticateUser(user, ApiName.Mal),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static UserConfig CreateConfig(Guid userId, string marker) => new() {
        UserId = userId, LibraryToCheck = [],
        UserApiAuth = [
            new UserApiAuth { Name = ApiName.Mal, AccessToken = marker + "-mal-fixture", RefreshToken = marker + "-refresh-fixture" },
            new UserApiAuth { Name = ApiName.AniList, AccessToken = marker + "-anilist-fixture" }
        ],
        KeyPairs = [new KeyPairs { Key = "FutureKey", Value = marker }]
    };

    private static void SetPermission(User user, PermissionKind kind, bool value)
    {
        foreach (var permission in user.Permissions.Where(permission => permission.Kind == kind).ToArray()) {
            user.Permissions.Remove(permission);
        }
        user.Permissions.Add(new Permission(kind, value));
    }

    private void SetHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _httpClient = new HttpClient(new HttpHandler(handler));
        _httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(_httpClient);
    }

    private static HttpResponseMessage Response(HttpRequestMessage? request, HttpStatusCode code, string content) => new(code) {
        RequestMessage = request, Content = new StringContent(content)
    };

    private static HttpResponseMessage TokenResponse(HttpRequestMessage? request) => Response(request, HttpStatusCode.OK,
        """{"access_token":"new-fixture-access","refresh_token":"new-fixture-refresh"}""");

    private static HttpResponseMessage KitsuProfileResponse(HttpRequestMessage request) => Response(request, HttpStatusCode.OK,
        """{"data":[{"id":"42","type":"users","attributes":{"id":42,"name":"selected profile","type":"users"}}]}""");

    private sealed class HttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
