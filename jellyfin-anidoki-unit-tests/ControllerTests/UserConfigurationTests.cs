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

    [Test]
    public void AdministratorConfigurationGetReturnsSelectedUser()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _targetConfig.PlanToWatchOnly = false;
        var result = (OkObjectResult)_controller.GetConfigurationUser(_target.Id);
        Assert.That(JsonSerializer.SerializeToElement(result.Value).GetProperty("PlanToWatchOnly").GetBoolean(), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UserConfigurationResponsesExcludeAuthenticationDetails(bool update)
    {
        var auth = _requesterConfig.UserApiAuth;
        var keyPairs = _requesterConfig.KeyPairs;
        var result = (OkObjectResult)(update
            ? _controller.UpdateConfigurationUser(_requester.Id, new UserEditableConfig { LibraryToCheck = [] })
            : _controller.GetConfigurationUser(_requester.Id));
        string json = JsonSerializer.Serialize(result.Value);
        Assert.Multiple(() => {
            Assert.That(result.Value!.GetType().Name, Is.EqualTo("UserConfigurationResponse"));
            Assert.That(json.Contains("AccessToken", StringComparison.OrdinalIgnoreCase), Is.False);
            Assert.That(json.Contains("RefreshToken", StringComparison.OrdinalIgnoreCase), Is.False);
            Assert.That(json.Contains("KeyPairs", StringComparison.OrdinalIgnoreCase), Is.False);
            Assert.That(json.Contains("requester-mal-fixture", StringComparison.Ordinal), Is.False);
            Assert.That(_requesterConfig.UserApiAuth, Is.SameAs(auth));
            Assert.That(_requesterConfig.KeyPairs, Is.SameAs(keyPairs));
            Assert.That(_requesterConfig.UserApiAuth[0].AccessToken == "requester-mal-fixture", Is.True);
        });
        var response = JsonSerializer.SerializeToElement(result.Value);
        Assert.That(response.GetProperty("ConnectedProviders").EnumerateArray().Select(value => (ApiName)value.GetInt32()),
            Is.EqualTo(new[] { ApiName.Mal, ApiName.AniList }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NewUserGetReturnsEffectiveDefaultsWithoutCreatingStoredRecords(bool nullConfiguration)
    {
        _plugin.Configuration.UserConfig = nullConfiguration ? null! : [_targetConfig];
        var result = (OkObjectResult)_controller.GetConfigurationUser(_requester.Id);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.That(json.TryGetProperty("PlanToWatchOnly", out var planToWatchOnly), Is.True);
        Assert.Multiple(() => {
            Assert.That(planToWatchOnly.GetBoolean(), Is.True);
            Assert.That(json.GetProperty("RewatchCompleted").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("ShowLogNotifications").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("LibraryToCheck").GetArrayLength(), Is.Zero);
            Assert.That(json.GetProperty("ConnectedProviders").GetArrayLength(), Is.Zero);
            Assert.That(_plugin.Configuration.UserConfig, nullConfiguration ? Is.Null : Is.EqualTo(new[] { _targetConfig }));
        });
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void UserSavePersistsExplicitNotificationPreference(bool create, bool notifications)
    {
        if (create) _plugin.Configuration.UserConfig = [_targetConfig];
        var dto = JsonSerializer.Deserialize<UserEditableConfig>(
            $"{{\"PlanToWatchOnly\":true,\"RewatchCompleted\":true,\"LibraryToCheck\":[],\"ShowLogNotifications\":{notifications.ToString().ToLowerInvariant()}}}")!;
        var result = (OkObjectResult)_controller.UpdateConfigurationUser(_requester.Id, dto);
        var savedUser = _plugin.Configuration.UserConfig.Single(config => config.UserId == _requester.Id);
        var savedJson = JsonSerializer.SerializeToElement(savedUser);
        Assert.That(savedJson.TryGetProperty("ShowLogNotifications", out var savedPreference), Is.True);
        Assert.Multiple(() => {
            Assert.That(savedPreference.GetBoolean(), Is.EqualTo(notifications));
            Assert.That(JsonSerializer.SerializeToElement(result.Value).GetProperty("ShowLogNotifications").GetBoolean(), Is.EqualTo(notifications));
            Assert.That(_plugin.Configuration.UserConfig.Single(config => config.UserId == _target.Id), Is.SameAs(_targetConfig));
        });
    }

    [TestCase(ApiName.Mal)]
    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Shikimori)]
    [TestCase(ApiName.Simkl)]
    public void IncompleteOAuthAppIsUnavailableToUser(ApiName provider)
    {
        _plugin.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = provider, ClientId = "fixture-client", ClientSecret = "" }];
        var parameters = (Parameters)_controller.GetFrontendParametersUser([ParameterInclude.ProviderList]);
        var available = parameters.providerList.Select(item => (ApiName)((IDictionary<string, object?>)item)["Key"]!);
        Assert.That(available, Is.EqualTo(new[] { ApiName.Kitsu }));
    }

    [Test]
    public void ShikimoriWithoutAppNameIsUnavailableToUser()
    {
        _plugin.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Shikimori, ClientId = "fixture-client", ClientSecret = "fixture-secret" }];
        var parameters = (Parameters)_controller.GetFrontendParametersUser([ParameterInclude.ProviderList]);
        Assert.That(parameters.providerList.Select(item => (ApiName)((IDictionary<string, object?>)item)["Key"]!),
            Is.EqualTo(new[] { ApiName.Kitsu }));
    }

    [Test]
    public void AdministratorConfigurationPutChangesSelectedPreferencesAndPreservesAuthentication()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        var requesterAuth = _requesterConfig.UserApiAuth;
        var targetAuth = _targetConfig.UserApiAuth;
        var targetKeyPairs = _targetConfig.KeyPairs;
        var result = _controller.UpdateConfigurationUser(_target.Id, new UserEditableConfig {
            PlanToWatchOnly = false, RewatchCompleted = false, LibraryToCheck = [_libraryId.ToString()]
        });

        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(_targetConfig.PlanToWatchOnly, Is.False);
            Assert.That(_targetConfig.RewatchCompleted, Is.False);
            Assert.That(_targetConfig.LibraryToCheck, Is.EqualTo(new[] { _libraryId.ToString() }));
            Assert.That(_targetConfig.UserApiAuth, Is.SameAs(targetAuth));
            Assert.That(_targetConfig.KeyPairs, Is.SameAs(targetKeyPairs));
            Assert.That(_targetConfig.UserApiAuth[0].AccessToken == "target-mal-fixture", Is.True);
            Assert.That(_targetConfig.UserApiAuth[0].RefreshToken == "target-refresh-fixture", Is.True);
            Assert.That(_targetConfig.KeyPairs.Single(pair => pair.Key == "FutureKey").Value, Is.EqualTo("target"));
            Assert.That(_requesterConfig.PlanToWatchOnly, Is.True);
            Assert.That(_requesterConfig.RewatchCompleted, Is.True);
            Assert.That(_requesterConfig.LibraryToCheck, Is.Empty);
            Assert.That(_requesterConfig.UserApiAuth, Is.SameAs(requesterAuth));
            Assert.That(_requesterConfig.UserApiAuth[0].AccessToken == "requester-mal-fixture", Is.True);
            Assert.That(_plugin.Configuration.UserConfig, Is.EqualTo(new[] { _requesterConfig, _targetConfig }));
        });
    }

    [Test]
    public async Task AdministratorProfileTestUsesSelectedAuthentication()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        bool targetAuthenticationUsed = false;
        SetHttp(request => {
            targetAuthenticationUsed = request.Headers.Authorization?.Parameter == _targetConfig.UserApiAuth[0].AccessToken;
            return Task.FromResult(Response(request, HttpStatusCode.OK, """
                {"id":42,"name":"selected profile","location":"","joined_at":"2020-01-01T00:00:00Z","picture":""}
                """));
        });
        var result = await _controller.GetUserUser(ApiName.Mal, _target.Id);
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(targetAuthenticationUsed, Is.True);
        });
    }

    [Test]
    public void AdministratorAuthorizationUrlStoresSelectedUserInState()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        var result = (OkObjectResult)_controller.BuildAuthorizeRequestUrlUser(ApiName.Mal, _target.Id);
        var url = new Uri((string)result.Value!);
        var state = QueryHelpers.ParseQuery(url.Query)["state"].ToString();
        Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, state)?.UserId, Is.EqualTo(_target.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AdministratorDeauthenticationRemovesOnlySelectedProvider(bool elevatedRoute)
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        var result = elevatedRoute
            ? _controller.Deauthenticate(_target.Id, ApiName.Mal)
            : _controller.DeauthenticateUser(_target.Id, ApiName.Mal);
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(_targetConfig.UserApiAuth.Select(auth => auth.Name), Is.EqualTo(new[] { ApiName.AniList }));
            Assert.That(_requesterConfig.UserApiAuth.Select(auth => auth.Name), Is.EqualTo(new[] { ApiName.Mal, ApiName.AniList }));
        });
    }

    [TestCase("get")]
    [TestCase("put")]
    [TestCase("profile")]
    [TestCase("authorize")]
    [TestCase("grant")]
    [TestCase("deauthenticate")]
    public async Task OrdinaryUserCannotOperateOnAnotherUser(string operation)
    {
        Assert.That(await InvokeUserOperation(operation, _target.Id), Is.TypeOf<ForbidResult>());
    }

    [TestCase("get")]
    [TestCase("put")]
    [TestCase("profile")]
    [TestCase("authorize")]
    [TestCase("grant")]
    [TestCase("deauthenticate")]
    public async Task DisabledUserPagesRejectUserOperations(string operation)
    {
        _plugin.Configuration.enableUserPages = false;
        Assert.That(await InvokeUserOperation(operation, _requester.Id), Is.TypeOf<NotFoundResult>());
    }

    [Test]
    public void NullLibrariesAreSavedAsEmptyArray()
    {
        var result = _controller.UpdateConfigurationUser(_requester.Id, new UserEditableConfig {
            LibraryToCheck = null!
        });
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(_requesterConfig.LibraryToCheck, Is.Not.Null.And.Empty);
        });
    }

    [TestCase("invalid")]
    [TestCase("")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    [TestCase(null)]
    public void InvalidLibraryIdsRejectSaveWithoutChangingPreferences(string? invalidId)
    {
        var result = _controller.UpdateConfigurationUser(_requester.Id, new UserEditableConfig {
            PlanToWatchOnly = false, RewatchCompleted = false,
            LibraryToCheck = [_libraryId.ToString(), invalidId!]
        });
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(_requesterConfig.PlanToWatchOnly, Is.True);
            Assert.That(_requesterConfig.RewatchCompleted, Is.True);
            Assert.That(_requesterConfig.LibraryToCheck, Is.Empty);
        });
    }

    [Test]
    public void AdministratorSaveChecksSelectedUsersLibraryAccess()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        SetPermission(_target, PermissionKind.EnableAllFolders, false);
        _target.Preferences.Clear();
        var result = _controller.UpdateConfigurationUser(_target.Id, new UserEditableConfig {
            PlanToWatchOnly = false, RewatchCompleted = false, LibraryToCheck = [_libraryId.ToString()]
        });
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<ForbidResult>());
            Assert.That(_targetConfig.PlanToWatchOnly, Is.True);
            Assert.That(_requesterConfig.PlanToWatchOnly, Is.True);
        });
    }

    [Test]
    public void EmptyLibrariesRemainEmptyArray()
    {
        Assert.That(_controller.UpdateConfigurationUser(_requester.Id,
            new UserEditableConfig { LibraryToCheck = [] }), Is.TypeOf<OkObjectResult>());
        Assert.That(_requesterConfig.LibraryToCheck, Is.Empty);
    }

    [Test]
    public void NewUserConfigurationIsCreatedWithoutReplacingOtherUsers()
    {
        _plugin.Configuration.UserConfig = [_targetConfig];
        Assert.That(_controller.UpdateConfigurationUser(_requester.Id,
            new UserEditableConfig { PlanToWatchOnly = true, RewatchCompleted = false, LibraryToCheck = [] }),
            Is.TypeOf<OkObjectResult>());
        Assert.Multiple(() => {
            Assert.That(_plugin.Configuration.UserConfig[0], Is.SameAs(_targetConfig));
            Assert.That(_plugin.Configuration.UserConfig[1].UserId, Is.EqualTo(_requester.Id));
            Assert.That(_plugin.Configuration.UserConfig[1].RewatchCompleted, Is.False);
        });
    }

    [Test]
    public async Task PasswordGrantWaitsForTokenBeforeReturningSuccess()
    {
        var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grantResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetHttp(request => {
            grantStarted.SetResult();
            return grantResponse.Task;
        });
        var linking = _controller.PasswordGrantAuthentication(ApiName.AniList, _requester.Id.ToString(), "fixture-user", "fixture-password");
        await grantStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool returnedBeforeGrant = linking.IsCompleted;
        grantResponse.SetResult(Response(null, HttpStatusCode.OK,
            """{"access_token":"new-fixture-access","refresh_token":"new-fixture-refresh"}"""));
        var result = await linking;
        Assert.Multiple(() => {
            Assert.That(returnedBeforeGrant, Is.False);
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(_requesterConfig.UserApiAuth.Single(auth => auth.Name == ApiName.AniList).AccessToken == "new-fixture-access", Is.True);
        });
    }

    [Test]
    public async Task FailedPasswordGrantReturnsFailure()
    {
        SetHttp(request => Task.FromResult(Response(request, HttpStatusCode.Unauthorized,
            """{"error":"invalid_grant"}""")));
        var previousAuth = _requesterConfig.UserApiAuth.Single(auth => auth.Name == ApiName.AniList).AccessToken;
        var result = await _controller.PasswordGrantAuthentication(ApiName.AniList, _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<ObjectResult>());
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(500));
            Assert.That(_requesterConfig.UserApiAuth.Single(auth => auth.Name == ApiName.AniList).AccessToken == previousAuth, Is.True);
        });
    }

    [Test]
    public async Task FailedKitsuGrantDoesNotRequestProfileUsingPreviousAuthentication()
    {
        _requesterConfig.AddUserApiAuth(new UserApiAuth {
            Name = ApiName.Kitsu, AccessToken = "previous-kitsu-fixture", RefreshToken = "previous-refresh-fixture"
        });
        int profileCalls = 0;
        SetHttp(request => {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Response(request, HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
            profileCalls++;
            return Task.FromResult(KitsuProfileResponse(request));
        });
        var result = await _controller.PasswordGrantAuthentication(ApiName.Kitsu,
            _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.Multiple(() => {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(500));
            Assert.That(profileCalls, Is.Zero);
            Assert.That(_requesterConfig.UserApiAuth.Single(auth => auth.Name == ApiName.Kitsu).AccessToken == "previous-kitsu-fixture", Is.True);
        });
    }

    [Test]
    public async Task PasswordGrantWithoutRequiredProviderConfigReturnsFailure()
    {
        var result = await _controller.PasswordGrantAuthentication(ApiName.Shikimori,
            _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<ObjectResult>());
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(500));
        });
    }

    [TestCase(ApiName.Mal)]
    [TestCase(ApiName.Kitsu)]
    public async Task InvalidSavedCallbackDoesNotBlockTokenRefresh(ApiName provider)
    {
        const string invalidAddress = "https://host?legacy=value";
        _plugin.Configuration.callbackUrl = invalidAddress;
        bool refreshParametersPreserved = false;
        SetHttp(async request => {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
            refreshParametersPreserved = request.Method == HttpMethod.Post &&
                form["grant_type"] == "refresh_token" && form["refresh_token"] == "fixture-refresh" &&
                !form.ContainsKey("redirect_uri");
            return TokenResponse(request);
        });
        var authentication = new ApiAuthentication(provider, _httpClientFactory.Object,
            new Mock<IServerApplicationHost>().Object,
            new HttpContextAccessor { HttpContext = _controller.HttpContext }, NullLoggerFactory.Instance,
            _memoryCache, new Mock<IAsyncDelayer>().Object,
            new ProviderApiAuth { ClientId = "fixture-client", ClientSecret = "fixture-secret" });
        var result = await authentication.GetToken(_requester.Id, refreshToken: "fixture-refresh");
        Assert.Multiple(() => {
            Assert.That(result.AccessToken == "new-fixture-access", Is.True);
            Assert.That(refreshParametersPreserved, Is.True);
            Assert.That(_requesterConfig.UserApiAuth.Single(auth => auth.Name == provider).AccessToken == "new-fixture-access", Is.True);
            Assert.That(_plugin.Configuration.callbackUrl, Is.EqualTo(invalidAddress));
        });
    }

    [TestCase("{}")]
    [TestCase("{\"error\":\"invalid_grant\"}")]
    [TestCase("{\"access_token\":null}")]
    [TestCase("{\"access_token\":\"\"}")]
    [TestCase("{\"access_token\":\" \"}")]
    [TestCase("null")]
    public void SuccessfulHttpResponseWithoutAccessTokenFailsBeforeOverwritingAuthentication(string body)
    {
        SetHttp(request => Task.FromResult(Response(request, HttpStatusCode.OK, body)));
        var authentication = new ApiAuthentication(ApiName.Mal, _httpClientFactory.Object,
            _serverHost.Object, new HttpContextAccessor { HttpContext = _controller.HttpContext },
            NullLoggerFactory.Instance, _memoryCache, new Mock<IAsyncDelayer>().Object,
            new ProviderApiAuth { ClientId = "fixture-client", ClientSecret = "fixture-secret" });
        Assert.That(async () => await authentication.GetToken(_requester.Id, refreshToken: "fixture-refresh"),
            Throws.TypeOf<AuthenticationException>());
        Assert.Multiple(() => {
            Assert.That(_requesterConfig.UserApiAuth[0].AccessToken == "requester-mal-fixture", Is.True);
            Assert.That(_requesterConfig.UserApiAuth[0].RefreshToken == "requester-refresh-fixture", Is.True);
        });
    }

    [Test]
    public async Task InvalidSavedCallbackDoesNotBlockKitsuPasswordGrant()
    {
        const string invalidAddress = "https://host?legacy=value";
        _plugin.Configuration.callbackUrl = invalidAddress;
        int tokenCalls = 0;
        int profileCalls = 0;
        SetHttp(request => {
            if (request.Method == HttpMethod.Post) {
                tokenCalls++;
                return Task.FromResult(TokenResponse(request));
            }
            profileCalls++;
            return Task.FromResult(KitsuProfileResponse(request));
        });
        var result = await _controller.PasswordGrantAuthentication(ApiName.Kitsu,
            _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(tokenCalls, Is.EqualTo(1));
            Assert.That(profileCalls, Is.EqualTo(1));
            Assert.That(_requesterConfig.KeyPairs.SingleOrDefault(pair => pair.Key == "KitsuUserId")?.Value, Is.EqualTo("42"));
            Assert.That(_plugin.Configuration.callbackUrl, Is.EqualTo(invalidAddress));
        });
    }

    [Test]
    public void AuthorizationCodeGrantRejectsInvalidSavedCallbackBeforePosting()
    {
        _plugin.Configuration.callbackUrl = "https://host?legacy=value";
        int requests = 0;
        SetHttp(request => {
            requests++;
            return Task.FromResult(TokenResponse(request));
        });
        Assert.That(async () => {
            var authentication = new ApiAuthentication(ApiName.Mal, _httpClientFactory.Object,
                new Mock<IServerApplicationHost>().Object,
                new HttpContextAccessor { HttpContext = _controller.HttpContext }, NullLoggerFactory.Instance,
                _memoryCache, new Mock<IAsyncDelayer>().Object,
                new ProviderApiAuth { ClientId = "fixture-client", ClientSecret = "fixture-secret" });
            await authentication.GetToken(_requester.Id, code: "fixture-code");
        }, Throws.ArgumentException);
        Assert.That(requests, Is.Zero);
    }

    [Test]
    public async Task AuthorizationCodeGrantUsesSameCallbackAsPreview()
    {
        const string baseAddress = "https://host/jellyfin/";
        _plugin.Configuration.callbackUrl = baseAddress;
        string? postedCallback = null;
        SetHttp(async request => {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
            postedCallback = form["redirect_uri"].ToString();
            return TokenResponse(request);
        });
        var authentication = new ApiAuthentication(ApiName.Mal, _httpClientFactory.Object,
            new Mock<IServerApplicationHost>().Object,
            new HttpContextAccessor { HttpContext = _controller.HttpContext }, NullLoggerFactory.Instance,
            _memoryCache, new Mock<IAsyncDelayer>().Object,
            new ProviderApiAuth { ClientId = "fixture-client", ClientSecret = "fixture-secret" });
        await authentication.GetToken(_requester.Id, code: "fixture-code");
        var preview = System.Text.Json.JsonSerializer.SerializeToElement(((OkObjectResult)_controller.CallbackPreview(baseAddress)).Value);
        Assert.That(postedCallback, Is.EqualTo(preview.GetProperty("callbackUrl").GetString()));
    }

    [Test]
    public async Task KitsuGrantInitializesMissingKeyPairsAndPreservesGrantParameters()
    {
        _requesterConfig.KeyPairs = null!;
        bool grantParametersPreserved = false;
        bool tokenAvailableDuringLookup = false;
        SetHttp(async request => {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/token")) {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
                grantParametersPreserved = form["grant_type"] == "password" && form["username"] == "fixture-user" && form["password"] == "fixture-password";
                return TokenResponse(request);
            }
            tokenAvailableDuringLookup = request.Headers.Authorization?.Parameter == "new-fixture-access" &&
                _requesterConfig.UserApiAuth.Any(auth => auth.Name == ApiName.Kitsu && auth.AccessToken == "new-fixture-access");
            return KitsuProfileResponse(request);
        });
        var result = await _controller.PasswordGrantAuthentication(ApiName.Kitsu, _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(grantParametersPreserved, Is.True);
            Assert.That(tokenAvailableDuringLookup, Is.True);
            Assert.That(_requesterConfig.KeyPairs.Single(pair => pair.Key == "KitsuUserId").Value, Is.EqualTo("42"));
            Assert.That(_requesterConfig.UserApiAuth.Length, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task AdministratorKitsuGrantWaitsForTokenAndCapturesSelectedUserId()
    {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grantResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int profileCalls = 0;
        bool tokenAvailableDuringLookup = false;
        SetHttp(request => {
            if (request.Method == HttpMethod.Post) {
                grantStarted.SetResult();
                return grantResponse.Task;
            }
            profileCalls++;
            tokenAvailableDuringLookup = request.Headers.Authorization?.Parameter == "new-fixture-access" &&
                _targetConfig.UserApiAuth.Any(auth => auth.Name == ApiName.Kitsu && auth.AccessToken == "new-fixture-access");
            return Task.FromResult(KitsuProfileResponse(request));
        });
        var linking = _controller.PasswordGrantAuthenticationUser(ApiName.Kitsu, _target.Id, "fixture-user", "fixture-password");
        await grantStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool returnedBeforeGrant = linking.IsCompleted;
        grantResponse.SetResult(TokenResponse(null));
        var result = await linking;
        Assert.Multiple(() => {
            Assert.That(returnedBeforeGrant, Is.False);
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(profileCalls, Is.EqualTo(1));
            Assert.That(tokenAvailableDuringLookup, Is.True);
            Assert.That(_targetConfig.KeyPairs.Single(pair => pair.Key == "KitsuUserId").Value, Is.EqualTo("42"));
            Assert.That(_targetConfig.KeyPairs.Any(pair => pair.Key == "FutureKey"), Is.True);
            Assert.That(_requesterConfig.UserApiAuth.Any(auth => auth.Name == ApiName.Kitsu), Is.False);
        });
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
