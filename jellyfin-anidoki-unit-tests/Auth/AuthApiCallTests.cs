using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Trackers.AniList;
using jellyfin_anidoki_unit_tests.Trackers.Shikimori;
using jellyfin_anidoki_unit_tests.Trackers.Simkl;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Auth;

public class AuthApiCallTests {
    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            return Task.FromResult(responses[Math.Min(Requests.Count, responses.Length) - 1](request));
        }
    }

    private static AuthApiCall Create(HttpMessageHandler handler) {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        return new AuthApiCall(factory.Object, new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new NullLoggerFactory(), new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = new[] { new UserApiAuth { Name = ApiName.AniList, AccessToken = "accessToken", RefreshToken = "refreshToken" } }
            });
    }

    /// <summary>
    /// A request that throws never got a response. It used to fall through to a default 200 OK and be reported as a
    /// successful call.
    /// </summary>
    [Test]
    public async Task NetworkErrorIsNotReportedAsSuccess() {
        var handler = new SequenceHandler(_ => throw new HttpRequestException("connection reset"));

        var response = await Create(handler).AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.POST, "https://graphql.anilist.co", stringContent: new StringContent("{}"));

        Assert.That(response, Is.Null);
    }

    /// <summary>
    /// Extra request headers must be sent once per request, including on a retry after a rate limit.
    /// </summary>
    [Test]
    public async Task RetryAfterRateLimitSendsRequestHeadersOnce() {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Create(handler).AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.GET, "https://graphql.anilist.co",
            requestHeaders: new Dictionary<string, string> { { "X-Fixture", "value" } });

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.IsSuccessStatusCode, Is.True);
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        foreach (var request in handler.Requests) {
            Assert.That(request.Headers.GetValues("X-Fixture").Count(), Is.EqualTo(1));
        }
    }

    private static async Task<(TrackerFailureKind? Kind, bool ResponseReceived)> Reported(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) {
        using var calls = TrackerCallScope.Begin();
        var response = await Create(new SequenceHandler(responses)).AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.GET, "https://graphql.anilist.co");
        Assert.That(response, Is.Null);
        return (calls.Failure, calls.ResponseReceived);
    }

    [TestCase(HttpStatusCode.NotFound, TrackerFailureKind.NotFound)]
    [TestCase(HttpStatusCode.InternalServerError, TrackerFailureKind.Transient)]
    [TestCase(HttpStatusCode.BadGateway, TrackerFailureKind.Transient)]
    [TestCase(HttpStatusCode.RequestTimeout, TrackerFailureKind.Transient)]
    [TestCase(HttpStatusCode.BadRequest, TrackerFailureKind.Rejected)]
    [TestCase(HttpStatusCode.Forbidden, TrackerFailureKind.Rejected)]
    public async Task AFailedStatusReportsItsKind(HttpStatusCode status, TrackerFailureKind expected) =>
        Assert.That(await Reported(_ => new HttpResponseMessage(status)), Is.EqualTo(((TrackerFailureKind?)expected, true)));

    [Test]
    public async Task ExhaustedRateLimitRetriesReportRateLimited() =>
        Assert.That(await Reported(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)),
            Is.EqualTo(((TrackerFailureKind?)TrackerFailureKind.RateLimited, true)));

    [Test]
    public async Task AFailedRefreshReportsUnauthorized() =>
        Assert.That(await Reported(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            Is.EqualTo(((TrackerFailureKind?)TrackerFailureKind.Unauthorized, true)));

    /// <summary>
    /// AniList answers a revoked or invalid token with 400 "Invalid token", not 401, so it must read as a lost sign-in
    /// (attention and Sign in again), not as a rejected request.
    /// </summary>
    [Test]
    public async Task AniListInvalidTokenReportsUnauthorized() =>
        Assert.That(await Reported(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) {
            Content = new StringContent("""{"errors":[{"message":"Invalid token","status":400,"locations":[{"line":1,"column":1}]}],"data":null}""")
        }), Is.EqualTo(((TrackerFailureKind?)TrackerFailureKind.Unauthorized, true)));

    [Test]
    public async Task AniListOtherBadRequestStaysRejected() =>
        Assert.That(await Reported(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) {
            Content = new StringContent("""{"errors":[{"message":"Validation error","status":400}],"data":null}""")
        }), Is.EqualTo(((TrackerFailureKind?)TrackerFailureKind.Rejected, true)));

    [Test]
    public async Task NoResponseReportsTransientAndSaysSo() =>
        Assert.That(await Reported(_ => throw new HttpRequestException("connection reset")),
            Is.EqualTo(((TrackerFailureKind?)TrackerFailureKind.Transient, false)));

    [Test]
    public async Task ASuccessfulRetryReportsNothing() {
        using var calls = TrackerCallScope.Begin();
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.OK));

        await Create(handler).AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.GET, "https://graphql.anilist.co");

        Assert.That(calls.Failure, Is.Null);
    }

    [Test]
    public void AnOuterScopeSurvivesAnInnerOne() {
        using var outer = TrackerCallScope.Begin();
        using (var inner = TrackerCallScope.Begin()) {
            TrackerCallScope.Report(TrackerFailureKind.Transient);
            Assert.That(inner.Failure, Is.EqualTo(TrackerFailureKind.Transient));
        }

        Assert.That(outer.Failure, Is.Null);
        TrackerCallScope.Report(TrackerFailureKind.Rejected);
        Assert.That(outer.Failure, Is.EqualTo(TrackerFailureKind.Rejected));
    }

    [Test]
    public void ANotFoundNeverHidesADefiniteFailure() {
        using var calls = TrackerCallScope.Begin();
        TrackerCallScope.Report(TrackerFailureKind.NotFound);
        TrackerCallScope.Report(TrackerFailureKind.Transient);
        TrackerCallScope.Report(TrackerFailureKind.NotFound);
        Assert.That(calls.Failure, Is.EqualTo(TrackerFailureKind.Transient));
    }

    /// <summary>A client built without a user has no linked account to call with; that reads as not linked, not a crash.</summary>
    [Test]
    public async Task AClientWithNoUserReadsAsNotLinked() {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        var call = new AuthApiCall(factory.Object, new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new NullLoggerFactory(), new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, null);

        Assert.That(await call.AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.GET, "https://tracker.example/"), Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Shikimori gives no token without an app name. A refresh that comes back with none fails the call like any failed
    /// refresh, instead of retrying with no token.
    /// </summary>
    [Test]
    public async Task ARefreshThatGivesNoTokenFailsTheCall() {
        using var harness = new SyncHarness();
        harness.Configuration.shikimoriAppName = null;
        harness.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Shikimori, ClientId = "shikimori-client" }];
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        var call = new AuthApiCall(factory.Object, new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object,
            new NullLoggerFactory(), new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, new UserConfig {
                UserApiAuth = [new UserApiAuth { Name = ApiName.Shikimori, AccessToken = "expired", RefreshToken = "refresh" }]
            });

        using var calls = TrackerCallScope.Begin();
        Assert.That(await call.AuthenticatedApiCall(ApiName.Shikimori, AuthApiCall.CallType.GET, "https://shikimori.example/api"), Is.Null);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(calls.Failure, Is.EqualTo(TrackerFailureKind.Unauthorized));
    }

    /// <summary>Before any user saved settings there is no user list; signing in reports the missing user, as it does for an unknown one.</summary>
    [Test]
    public void GettingATokenOnANewInstallReportsTheMissingUser() {
        using var harness = new SyncHarness();
        harness.Configuration.UserConfig = null;
        var authentication = new ApiAuthentication(ApiName.Simkl, Helpers.Sequence((HttpStatusCode.OK, """{"access_token":"new-access"}""")),
            new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object, new NullLoggerFactory(),
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, overrideRedirectUrl: "https://jf.example");

        var error = Assert.ThrowsAsync<NullReferenceException>(() => authentication.GetToken(Guid.NewGuid(), code: "code"));
        Assert.That(error!.Message, Does.Contain("does not exist"));
    }
}
