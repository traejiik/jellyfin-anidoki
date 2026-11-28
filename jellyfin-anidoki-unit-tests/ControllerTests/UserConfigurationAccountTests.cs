using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

public partial class UserConfigurationTests {
    [Test]
    public async Task BodyPasswordGrantUsesSelectedUserWithoutUserPageGate() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.enableUserPages = false;
        var otherAuth = _requesterConfig.UserApiAuth;
        var appAuth = _plugin.Configuration.ProviderApiAuth;
        SetSuccessfulKitsuHttp();
        var result = await InvokeEndpoint("PasswordGrantAuthenticationBody", GrantRequest(_target.Id));
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<OkResult>());
            Assert.That(_targetConfig.UserApiAuth!.Any(auth => auth.Name == ApiName.Kitsu && auth.AccessToken == "new-fixture-access"), Is.True);
            Assert.That(_targetConfig.KeyPairs!.Single(pair => pair.Key == "KitsuUserId").Value, Is.EqualTo("42"));
            Assert.That(_targetConfig.UserApiAuth![0].AccessToken == "target-mal-fixture", Is.True);
            Assert.That(_requesterConfig.UserApiAuth, Is.SameAs(otherAuth));
            Assert.That(_plugin.Configuration.ProviderApiAuth, Is.SameAs(appAuth));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BodyPasswordGrantDeniesOrdinaryCrossUserAccess(bool userRoute) {
        var result = await InvokeEndpoint(userRoute ? "PasswordGrantAuthenticationUserBody" : "PasswordGrantAuthenticationBody", GrantRequest(_target.Id));
        Assert.That(result, Is.TypeOf<ForbidResult>());
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UserBodyPasswordGrantRemainsGated() {
        _plugin.Configuration.enableUserPages = false;
        Assert.That(await InvokeEndpoint("PasswordGrantAuthenticationUserBody", GrantRequest(_requester.Id)), Is.TypeOf<NotFoundResult>());
    }

    [TestCase("body")]
    [TestCase("provider")]
    [TestCase("user")]
    [TestCase("username")]
    [TestCase("password")]
    public async Task BodyPasswordGrantRejectsMissingOrUnsupportedValues(string invalidField) {
        var request = GrantRequest(_requester.Id);
        switch (invalidField) {
            case "provider":
                request.Provider = ApiName.Mal;
                break;
            case "user":
                request.User = Guid.Empty;
                break;
            case "username":
                request.Username = " ";
                break;
            case "password":
                request.Password = "";
                break;
        }
        Assert.That(await InvokeEndpoint("PasswordGrantAuthenticationBody", invalidField == "body" ? null : request),
            Is.TypeOf<BadRequestObjectResult>());
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [Test]
    public async Task BodyPasswordGrantRejectsDeletedSelectedUser() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        Assert.That(await InvokeEndpoint("PasswordGrantAuthenticationBody", GrantRequest(Guid.NewGuid())), Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task FirstBodyPasswordGrantCreatesOnlySelectedUsersDefaultConfig() {
        _plugin.Configuration.UserConfig = [_targetConfig];
        SetSuccessfulKitsuHttp();
        Assert.That(await InvokeEndpoint("PasswordGrantAuthenticationUserBody", GrantRequest(_requester.Id)), Is.TypeOf<OkResult>());
        var created = _plugin.Configuration.UserConfig.Single(config => config.UserId == _requester.Id);
        Assert.Multiple(() => {
            Assert.That(created.PlanToWatchOnly, Is.True);
            Assert.That(created.RewatchCompleted, Is.True);
            Assert.That(created.ShowLogNotifications, Is.True);
            Assert.That(created.LibraryToCheck, Is.Null, "a new user has no pre-2.0 library choice to seed from");
            Assert.That(_plugin.Configuration.UserConfig.Single(config => config.UserId == _target.Id), Is.SameAs(_targetConfig));
        });
    }

    [TestCase(ApiName.Mal)]
    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Shikimori)]
    [TestCase(ApiName.Simkl)]
    public async Task SavedOAuthAuthorizationUsesSelectedUserAndSavedAppCredentials(ApiName provider) {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.enableUserPages = false;
        ConfigureOAuthProvider(provider);
        var result = (OkObjectResult)await InvokeEndpoint("AuthorizeProvider", provider, _target.Id);
        string url = (string)result.Value!;
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        Assert.Multiple(() => {
            Assert.That(query["client_id"].ToString(), Is.EqualTo("saved-client"));
            Assert.That(url.Contains("saved-secret", StringComparison.Ordinal), Is.False);
            Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, query["state"].ToString())?.UserId, Is.EqualTo(_target.Id));
        });
        _httpClientFactory.VerifyNoOtherCalls();
    }

    [TestCase(ApiName.Kitsu)]
    [TestCase(ApiName.Annict)]
    [TestCase((ApiName)999)]
    public async Task SavedOAuthAuthorizationRejectsUnsupportedProviders(ApiName provider) {
        Assert.That(await InvokeEndpoint("AuthorizeProvider", provider, _requester.Id), Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SavedOAuthAuthorizationDeniesOrdinaryCrossUserAccess() {
        Assert.That(await InvokeEndpoint("AuthorizeProvider", ApiName.Mal, _target.Id), Is.TypeOf<ForbidResult>());
    }

    [TestCase("client-id")]
    [TestCase("secret")]
    [TestCase("app-name")]
    public async Task SavedOAuthAuthorizationRejectsIncompleteConfiguration(string missingField) {
        ConfigureOAuthProvider(ApiName.Shikimori);
        if (missingField == "client-id")
            _plugin.Configuration.ProviderApiAuth![0].ClientId = "";
        if (missingField == "secret")
            _plugin.Configuration.ProviderApiAuth![0].ClientSecret = " ";
        if (missingField == "app-name")
            _plugin.Configuration.shikimoriAppName = "";
        Assert.That(await InvokeEndpoint("AuthorizeProvider", ApiName.Shikimori, _requester.Id), Is.TypeOf<BadRequestObjectResult>());
        Assert.That(_auth.BuildAuthorizeRequestUrlUser(ApiName.Shikimori, _requester.Id), Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SavedOAuthAuthorizationCreatesSelectedDefaultConfigAfterValidation() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.UserConfig = [_requesterConfig];
        Assert.That(await InvokeEndpoint("AuthorizeProvider", ApiName.Mal, _target.Id), Is.TypeOf<OkObjectResult>());
        var created = _plugin.Configuration.UserConfig.Single(config => config.UserId == _target.Id);
        Assert.Multiple(() => {
            Assert.That(created.PlanToWatchOnly, Is.True);
            Assert.That(created.ShowLogNotifications, Is.True);
            Assert.That(_plugin.Configuration.UserConfig.Single(config => config.UserId == _requester.Id), Is.SameAs(_requesterConfig));
        });
    }

    [Test]
    public async Task InvalidSavedCallbackDoesNotCreateOAuthUserConfig() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.UserConfig = [_requesterConfig];
        _plugin.Configuration.callbackUrl = "https://host?invalid=value";
        Assert.That(await InvokeEndpoint("AuthorizeProvider", ApiName.Mal, _target.Id), Is.TypeOf<BadRequestObjectResult>());
        Assert.That(_plugin.Configuration.UserConfig, Is.EqualTo(new[] { _requesterConfig }));
    }

    [Test]
    public async Task ElevatedProfileAcceptsSelectedUserAlias() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.enableUserPages = false;
        bool targetAuthUsed = false;
        SetHttp(request => {
            targetAuthUsed = request.Headers.Authorization?.Parameter == "target-mal-fixture";
            return Task.FromResult(Response(request, HttpStatusCode.OK, """{"id":42,"name":"selected"}"""));
        });
        var method = typeof(SettingsController).GetMethod("GetUser")!;
        Assert.That(method.GetParameters().Any(parameter => parameter.Name == "user"), Is.True, "Elevated profile must accept user= alias");
        Assert.That(await InvokeEndpoint("GetUser", ApiName.Mal, null, _target.Id), Is.TypeOf<OkObjectResult>());
        Assert.That(targetAuthUsed, Is.True);
    }

    [Test]
    public async Task ElevatedLegacyProfileDeniesOrdinaryCrossUserAccess() {
        SetHttp(request => Task.FromResult(Response(request, HttpStatusCode.OK, """{"id":42,"name":"selected"}""")));
        Assert.That(await _settings.GetUser(ApiName.Mal, _target.Id.ToString()), Is.TypeOf<ForbidResult>());
    }

    // A reply without a viewer is a failed read, the same as no reply; it must never read as a linked account.
    [TestCase("{}")]
    [TestCase("""{"data":null}""")]
    [TestCase("""{"data":{"viewer":null}}""")]
    public async Task GetUserReportsAnAnnictReplyWithoutAViewerAsAFailedRead(string reply) {
        _requesterConfig.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = "requester-annict-fixture" });
        SetHttp(request => Task.FromResult(Response(request, HttpStatusCode.OK, reply)));
        var result = await _settings.GetUser(ApiName.Annict, null, _requester.Id);
        Assert.Multiple(() => {
            Assert.That(result, Is.TypeOf<ObjectResult>());
            Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
            Assert.That(((ObjectResult)result).Value, Is.EqualTo("Authentication failed"));
        });
    }

    [Test]
    public async Task GetUserReadsAnAnnictViewerWithoutAUsername() {
        _requesterConfig.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = "requester-annict-fixture" });
        SetHttp(request => Task.FromResult(Response(request, HttpStatusCode.OK, """{"data":{"viewer":{}}}""")));
        var result = await _settings.GetUser(ApiName.Annict, null, _requester.Id);
        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.That(((MalApiCalls.User)((OkObjectResult)result).Value!).Name, Is.Null);
    }

    // An admin settings save replaces the whole configuration; one posted from a page loaded before any user
    // existed has no user list. If it lands between the token save and the Kitsu lookup, the lookup must not throw.
    [Test]
    public async Task KitsuPasswordGrantSurvivesAUserListReplacedDuringSignIn() {
        SetSuccessfulKitsuHttp();
        _xmlSerializer.Setup(serializer => serializer.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback(() => _plugin.Configuration.UserConfig = null);
        var result = await _auth.PasswordGrantAuthentication(ApiName.Kitsu, _requester.Id.ToString(), "fixture-user", "fixture-password");
        Assert.That(result, Is.TypeOf<OkResult>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AnnictTokenChangesOnlySelectedUsersAnnictAuthentication(bool replace) {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        _plugin.Configuration.enableUserPages = false;
        _requesterConfig.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = "requester-annict-fixture" });
        if (replace)
            _targetConfig.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = "target-old-annict-fixture" });
        _targetConfig.PlanToWatchOnly = false;
        _targetConfig.ShowLogNotifications = false;
        var otherAuth = _requesterConfig.UserApiAuth;
        var targetKeys = _targetConfig.KeyPairs;
        var providerAuth = _plugin.Configuration.ProviderApiAuth;
        Assert.That(await InvokeEndpoint("SetAnnictToken", _target.Id, new AnnictTokenRequest { Token = "target-new-annict-fixture" }), Is.TypeOf<OkResult>());
        var targetAuth = _targetConfig.UserApiAuth;
        Assert.That(targetAuth, Is.Not.Null);
        Assert.Multiple(() => {
            Assert.That(targetAuth!.Count(auth => auth.Name == ApiName.Annict), Is.EqualTo(1));
            Assert.That(targetAuth!.Single(auth => auth.Name == ApiName.Annict).AccessToken == "target-new-annict-fixture", Is.True);
            Assert.That(targetAuth!.Single(auth => auth.Name == ApiName.Mal).AccessToken == "target-mal-fixture", Is.True);
            Assert.That(_requesterConfig.UserApiAuth, Is.SameAs(otherAuth));
            Assert.That(_targetConfig.KeyPairs, Is.SameAs(targetKeys));
            Assert.That(_targetConfig.PlanToWatchOnly, Is.False);
            Assert.That(_targetConfig.ShowLogNotifications, Is.False);
            Assert.That(_plugin.Configuration.ProviderApiAuth, Is.SameAs(providerAuth));
        });
    }

    [Test]
    public async Task AnnictTokenDeniesOrdinaryCrossUserAccess() {
        Assert.That(await InvokeEndpoint("SetAnnictToken", _target.Id, new AnnictTokenRequest { Token = "fixture-annict" }), Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task AnnictTokenRejectsDeletedTarget() {
        SetPermission(_requester, PermissionKind.IsAdministrator, true);
        Assert.That(await InvokeEndpoint("SetAnnictToken", Guid.NewGuid(), new AnnictTokenRequest { Token = "fixture-annict" }), Is.TypeOf<ForbidResult>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public async Task AnnictTokenRejectsMissingValue(string? token) {
        Assert.That(await InvokeEndpoint("SetAnnictToken", _requester.Id, token == null ? null : new AnnictTokenRequest { Token = token }), Is.TypeOf<BadRequestObjectResult>());
    }

    [TestCase("styles.css", "text/css")]
    [TestCase("common.js", "application/javascript")]
    [TestCase("config-state.js", "application/javascript")]
    [TestCase("popup-state.js", "application/javascript")]
    [TestCase("progress-popup.js", "application/javascript")]
    [TestCase("progress-popup.css", "text/css")]
    [TestCase("user-settings.js", "application/javascript")]
    [TestCase("match-editor.js", "application/javascript")]
    [TestCase("match-editor-state.js", "application/javascript")]
    [TestCase("user-matches.js", "application/javascript")]
    public async Task PublicStaticAssetsRemainAvailableWhenUserPagesAreDisabled(string asset, string contentType) {
        _plugin.Configuration.enableUserPages = false;
        var result = (FileStreamResult)await InvokeEndpoint("GetAsset", asset);
        using var stream = result.FileStream;
        using var reader = new StreamReader(stream);
        Assert.Multiple(() => {
            Assert.That(result.ContentType, Is.EqualTo(contentType));
            Assert.That(reader.ReadToEnd(), Is.Not.Empty);
        });
        Assert.That(typeof(PagesController).GetMethod("GetAsset")!.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Not.Null);
    }

    [TestCase("Anidoki.css")]
    [TestCase("../CommonJs.js")]
    [TestCase("jellyfin_anidoki.Configuration.ConfigPage.html")]
    [TestCase("missing.js")]
    public async Task StaticAssetRouteRejectsNamesOutsideWhitelist(string asset) {
        Assert.That(await InvokeEndpoint("GetAsset", asset), Is.TypeOf<NotFoundResult>());
    }

    [TestCase("PasswordGrantAuthenticationBody", "passwordGrant", true)]
    [TestCase("PasswordGrantAuthenticationUserBody", "user/passwordGrant", false)]
    [TestCase("SetAnnictToken", "annictToken", true)]
    public void AccountSecretsBindFromPostBody(string action, string route, bool elevated) {
        var method = typeof(AuthController).GetMethod(action)!;
        Assert.That(method, Is.Not.Null);
        Assert.Multiple(() => {
            Assert.That(method!.GetCustomAttribute<HttpPostAttribute>()?.Template, Is.EqualTo(route));
            Assert.That(method.GetParameters().Any(parameter => parameter.GetCustomAttribute<FromBodyAttribute>() != null), Is.True);
            Assert.That(method.GetCustomAttribute<AuthorizeAttribute>(), Is.Not.Null);
            Assert.That(method.GetCustomAttribute<AuthorizeAttribute>()?.Policy, elevated ? Is.EqualTo(Policies.RequiresElevation) : Is.Null);
        });
    }

    private static PasswordGrantRequest GrantRequest(Guid user) => new() {
        Provider = ApiName.Kitsu,
        User = user,
        Username = "fixture-user",
        Password = "fixture-password"
    };

    private void ConfigureOAuthProvider(ApiName provider) {
        _plugin.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = provider, ClientId = "saved-client", ClientSecret = "saved-secret" }];
        _plugin.Configuration.shikimoriAppName = "fixture-application";
    }

    private void SetSuccessfulKitsuHttp() => SetHttp(request => Task.FromResult(
        request.Method == HttpMethod.Post ? TokenResponse(request) : KitsuProfileResponse(request)));

    private async Task<IActionResult> InvokeEndpoint(string action, params object?[] arguments) => action switch {
        nameof(AuthController.PasswordGrantAuthenticationBody) =>
            await _auth.PasswordGrantAuthenticationBody((PasswordGrantRequest?)arguments[0]),
        nameof(AuthController.PasswordGrantAuthenticationUserBody) =>
            await _auth.PasswordGrantAuthenticationUserBody((PasswordGrantRequest?)arguments[0]),
        nameof(AuthController.AuthorizeProvider) =>
            _auth.AuthorizeProvider((ApiName)arguments[0]!, (Guid)arguments[1]!),
        nameof(SettingsController.GetUser) =>
            await _settings.GetUser((ApiName)arguments[0]!, (string?)arguments[1], (Guid?)arguments[2]),
        nameof(AuthController.SetAnnictToken) =>
            _auth.SetAnnictToken((Guid)arguments[0]!, (AnnictTokenRequest?)arguments[1]),
        nameof(PagesController.GetAsset) => _pages.GetAsset((string)arguments[0]!),
        _ => throw new ArgumentException("Unknown test action", nameof(action))
    };
}
