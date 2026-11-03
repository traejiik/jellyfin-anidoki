using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

public partial class UserConfigurationTests {
    private const string ThisServer = "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6";
    private const string OtherServer = "9f8e7d6c5b4a43928170615243342516";

    [Test]
    public async Task DeclinedSignInShowsDeclinedPageWithoutContactingProvider() {
        bool contacted = false;
        SetHttp(request => { contacted = true; return Task.FromResult(TokenResponse(request)); });
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.Mal, "fixture-verifier");
        var result = (ContentResult)await _controller.AuthCallback(null, state, "access_denied");
        Assert.Multiple(() => {
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.ContentType, Does.StartWith("text/html"));
            Assert.That(result.Content, Does.Contain("MyAnimeList didn’t link your account").And.Contain("error: access_denied").And.Contain("requester"));
            Assert.That(contacted, Is.False);
            Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, state), Is.Null);
        });
    }

    [Test]
    public async Task ProviderErrorWithoutCodeIsNotTreatedAsDeclined() {
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.AniList);
        var result = (ContentResult)await _controller.AuthCallback(null, state, "server_error");
        Assert.Multiple(() => {
            Assert.That(result.StatusCode, Is.EqualTo(500));
            Assert.That(result.Content, Does.Contain("AniList didn’t accept the sign-in").And.Contain("error: server_error"));
        });
    }

    [Test]
    public async Task StateIssuedByAnotherServerShowsWrongServerPageWithThisServersRedirectUrl() {
        _serverHost.SetupGet(host => host.SystemId).Returns(ThisServer);
        _plugin.Configuration.callbackUrl = "https://jellyfin.example.test/base";
        string state = CallbackState.Create(OtherServer, ApiName.AniList);
        var result = (ContentResult)await _controller.AuthCallback("fixture-code", state);
        Assert.Multiple(() => {
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.Content, Does.Contain("This sign-in started on another server").And.Contain("AniList app’s redirect URL"));
            Assert.That(result.Content, Does.Contain("value=\"https://jellyfin.example.test/base/AniDoki/authCallback\""));
            Assert.That(result.Content, Does.Contain("provider-anilist.svg"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task UnknownStateFromThisServerOrWithoutMarkerShowsExpiredPage(bool marked) {
        _serverHost.SetupGet(host => host.SystemId).Returns(ThisServer);
        _plugin.Configuration.authenticationLinkExpireTimeMinutes = 120;
        string state = marked ? CallbackState.Create(ThisServer, ApiName.Simkl) : Guid.NewGuid().ToString("N");
        var result = (ContentResult)await _controller.AuthCallback("fixture-code", state);
        Assert.Multiple(() => {
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.Content, Does.Contain("This sign-in link has expired").And.Contain("older than 2 hours"));
            Assert.That(result.Content, Does.Contain("The link you used is no longer valid"));
            Assert.That(result.Content, marked ? Does.Contain("Select Link next to Simkl again") : Does.Contain("Select Link next to your tracker again"));
        });
    }

    [Test]
    public void AddressCheckIsAnUncachedPage() {
        var result = _controller.ApiUrlTest();
        Assert.Multiple(() => {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.ContentType, Does.StartWith("text/html"));
            Assert.That(result.Content, Does.Contain("This is the correct address").And.Match("assets/v/[0-9a-f]{32}/jellyfin\\.svg"));
            Assert.That(_controller.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [Test]
    public async Task MissingStateShowsExpiredPage() {
        var result = (ContentResult)await _controller.AuthCallback("fixture-code", null);
        Assert.That(result.Content, Does.Contain("This sign-in link has expired"));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task UnsavedLinkExpiryKeepsTheLinkUsableAndShowsTheDefault(long configured) {
        _plugin.Configuration.authenticationLinkExpireTimeMinutes = configured;
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.AniList);
        var declined = (ContentResult)await _controller.AuthCallback(null, state, "access_denied");
        var expired = (ContentResult)await _controller.AuthCallback("fixture-code", state);
        Assert.Multiple(() => {
            Assert.That(declined.Content, Does.Contain("AniList didn’t link your account"), "the link was still stored when the callback arrived");
            Assert.That(expired.Content, Does.Contain("older than 24 hours"));
        });
    }

    [TestCase(true, "#/configurationpage?name=AniDoki")]
    [TestCase(false, "#/userpluginsettings.html?pageUrl=/AniDoki/settings")]
    public async Task CallbackLeadsBackToThePageTheSignInStartedOn(bool fromAdmin, string expectedHref) {
        _plugin.Configuration.enableUserPages = true;
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.AniList, returnToAdmin: fromAdmin);
        var result = (ContentResult)await _controller.AuthCallback(null, state, "access_denied");
        Assert.That(result.Content, Does.Contain($"href=\"../web/index.html{expectedHref}\""));
    }

    [Test]
    public void AdminSignInLinksReturnToTheAdminPage() {
        ConfigureOAuthProvider(ApiName.AniList);
        var result = (OkObjectResult)_controller.AuthorizeProvider(ApiName.AniList, _requester.Id);
        string state = QueryHelpers.ParseQuery(new Uri((string)result.Value!).Query)["state"].ToString();
        Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, state)?.ReturnToAdmin, Is.True);
    }

    [Test]
    public void SignInLinksCarryThisServersMarkerAndProvider() {
        _serverHost.SetupGet(host => host.SystemId).Returns(ThisServer);
        var authentication = new ApiAuthentication(ApiName.Mal, _httpClientFactory.Object, _serverHost.Object,
            new HttpContextAccessor(), NullLoggerFactory.Instance, _memoryCache, new Mock<IAsyncDelayer>().Object);
        string state = QueryHelpers.ParseQuery(new Uri(authentication.BuildAuthorizeRequestUrl(_requester.Id)).Query)["state"].ToString();
        Assert.Multiple(() => {
            Assert.That(CallbackState.TryReadOrigin(state, out string marker, out ApiName? provider), Is.True);
            Assert.That(marker, Is.EqualTo(ThisServer[..8]));
            Assert.That(provider, Is.EqualTo(ApiName.Mal));
            Assert.That(CallbackState.IsFromOtherServer(state, ThisServer), Is.False);
            Assert.That(CallbackState.IsFromOtherServer(state, OtherServer), Is.True);
            Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, state)?.CodeVerifier, Is.Not.Null);
        });
    }
}

public class CallbackPageTests {
    private static CallbackPageModel Model(CallbackOutcome outcome, string? user = "fixture-user", string? errorCode = null) =>
        new(outcome, ApiName.AniList, user, "assets/v/fixture/", "../web/index.html#/configurationpage?name=AniDoki", "https://host/AniDoki/authCallback", errorCode);

    [Test]
    public void JellyfinUserNameIsHtmlEncoded() {
        string html = CallbackPage.Render(Model(CallbackOutcome.Linked, "<img src=x onerror=alert(1)>"));
        Assert.That(html, Does.Not.Contain("<img src=x").And.Contain("&lt;img src=x onerror=alert(1)&gt;"));
    }

    [TestCase("invalid_client", "invalid_client")]
    [TestCase("access_denied", "access_denied")]
    [TestCase("<script>alert(1)</script>", null)]
    [TestCase("a very long error description with spaces", null)]
    [TestCase("", null)]
    public void OnlyShortErrorTokensAreKept(string code, string? expected) =>
        Assert.That(CallbackPage.SafeErrorCode(code), Is.EqualTo(expected));

    [Test]
    public void ErrorCodeIsReadFromAFailedTokenResponseWithoutItsDescription() {
        string message = "BadRequest - {\"error\":\"invalid_grant\",\"error_description\":\"fixture-provider-detail\"}";
        string? code = CallbackPage.ErrorCodeFrom(message);
        string html = CallbackPage.Render(Model(CallbackOutcome.ProviderError, errorCode: code));
        Assert.Multiple(() => {
            Assert.That(code, Is.EqualTo("invalid_grant"));
            Assert.That(html, Does.Contain("error: invalid_grant").And.Not.Contain("fixture-provider-detail"));
        });
    }

    [TestCase(CallbackOutcome.Linked, 200, "doki-cb-success")]
    [TestCase(CallbackOutcome.Expired, 400, "doki-cb-warning")]
    [TestCase(CallbackOutcome.WrongServer, 400, "doki-cb-warning")]
    [TestCase(CallbackOutcome.Declined, 400, "doki-cb-error")]
    [TestCase(CallbackOutcome.ProviderError, 500, "doki-cb-error")]
    public void EveryOutcomeRendersTheSharedPage(CallbackOutcome outcome, int status, string tone) {
        string html = CallbackPage.Render(Model(outcome));
        Assert.Multiple(() => {
            Assert.That(CallbackPage.StatusCode(outcome), Is.EqualTo(status));
            Assert.That(html, Does.Contain($"doki-cb-link {tone}"));
            Assert.That(html, Does.Contain("assets/v/fixture/jellyfin.svg").And.Contain("assets/v/fixture/jellyfin-banner.svg").And.Contain("assets/v/fixture/anidoki-symbol.svg"));
            Assert.That(html, Does.Not.Contain("{{"), "every placeholder is filled");
            Assert.That(html, Does.Not.Match(@"class=""[^""]*\bad-"), "no ad-blocker-hidden class names");
        });
    }

    [TestCase(CallbackOutcome.Linked, "fixture-user", new[] { "Tracker", "Jellyfin account", "Result" })]
    [TestCase(CallbackOutcome.Declined, "fixture-user", new[] { "Tracker", "Jellyfin account", "Result" })]
    [TestCase(CallbackOutcome.ProviderError, "fixture-user", new[] { "Tracker", "Jellyfin account", "Result" })]
    [TestCase(CallbackOutcome.Expired, null, new[] { "Tracker", "Result" })]
    [TestCase(CallbackOutcome.WrongServer, null, new[] { "Tracker", "Result" })]
    public void FactsTableListsWhatThePageKnows(CallbackOutcome outcome, string? user, string[] labels) {
        string html = CallbackPage.Render(Model(outcome, user));
        var found = Regex.Matches(html, "<dt>(.*?)</dt>").Select(match => match.Groups[1].Value);
        Assert.That(found, Is.EqualTo(labels));
    }

    [Test]
    public void UnknownTrackerHasNoFactsTable() {
        string html = CallbackPage.Render(Model(CallbackOutcome.Expired, null) with { Provider = null });
        Assert.Multiple(() => {
            Assert.That(html, Does.Not.Contain("<dl"));
            Assert.That(html, Does.Contain("doki-cb-unknown").And.Contain("Select Link next to your tracker again"));
        });
    }

    [Test]
    public void AddressCheckShowsOnlyTheConfirmation() {
        string html = CallbackPage.RenderAddressCheck("assets/v/fixture/");
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("This is the correct address").And.Contain("You can close this tab."));
            Assert.That(html, Does.Contain("assets/v/fixture/jellyfin.svg").And.Contain("assets/v/fixture/anidoki-symbol.svg"));
            Assert.That(html, Does.Not.Contain("class=\"doki-cb-tile doki-cb-provider").And.Not.Contain("class=\"doki-cb-link").And.Not.Contain("class=\"doki-cb-primary"));
            Assert.That(html, Does.Not.Contain("{{"), "every placeholder is filled");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ResultPagesInheritTheServersCustomCss(bool addressCheck) {
        string html = addressCheck ? CallbackPage.RenderAddressCheck("assets/v/fixture/") : CallbackPage.Render(Model(CallbackOutcome.Linked));
        Assert.That(html, Does.Contain("@import url(\"../Branding/Css.css\") layer(theme);"));
    }

    [TestCase(30, "30 minutes")]
    [TestCase(60, "1 hour")]
    [TestCase(120, "2 hours")]
    [TestCase(1440, "24 hours")]
    [TestCase(2880, "2 days")]
    [TestCase(90, "90 minutes")]
    public void LinkExpiryReadsNaturally(long minutes, string text) =>
        Assert.That(CallbackPage.Duration(minutes), Is.EqualTo(text));
}

public class CallbackStateTests {
    [Test]
    public void StateWithoutAServerIdIsAPlainKey() {
        string state = CallbackState.Create(null, ApiName.AniList);
        Assert.Multiple(() => {
            Assert.That(state, Has.Length.EqualTo(32));
            Assert.That(CallbackState.TryReadOrigin(state, out _, out _), Is.False);
            Assert.That(CallbackState.IsFromOtherServer(state, "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6"), Is.False);
        });
    }

    [TestCase("not-a-state")]
    [TestCase("0123456789abcdef0123456789abcdef-zzzzzzzz-Mal")]
    [TestCase("0123456789abcdef0123456789abcdef-1a2b3c4d")]
    public void MalformedStatesAreNotReadAsIssuedElsewhere(string state) =>
        Assert.That(CallbackState.IsFromOtherServer(state, "1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6"), Is.False);

    [Test]
    public void UnknownProviderNameKeepsTheServerMarker() {
        Assert.Multiple(() => {
            Assert.That(CallbackState.TryReadOrigin("0123456789abcdef0123456789abcdef-1a2b3c4d-Nope", out string marker, out ApiName? provider), Is.True);
            Assert.That(marker, Is.EqualTo("1a2b3c4d"));
            Assert.That(provider, Is.Null);
        });
    }

    [Test]
    public void EachStateIsUnique() {
        var states = Enumerable.Range(0, 50).Select(_ => CallbackState.Create("1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", ApiName.Mal)).ToHashSet();
        Assert.That(states, Has.Count.EqualTo(50));
    }
}
