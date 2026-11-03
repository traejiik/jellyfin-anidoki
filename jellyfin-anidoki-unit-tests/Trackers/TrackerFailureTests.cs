using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Api.Anilist;
using jellyfin_anidoki.Api.Annict;
using jellyfin_anidoki.Api.Kitsu;
using jellyfin_anidoki.Api.Shikimori;
using jellyfin_anidoki.Api.Simkl;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Trackers.TrackerTestSupport;

namespace jellyfin_anidoki_unit_tests.Trackers;

[TestFixture]
public class TrackerFailureTests {
    private sealed class FixedResponse(Func<HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static ApiCallHelpersTrackerProvider Real(ApiName provider, Func<HttpResponseMessage> respond) {
        var handler = new FixedResponse(respond);
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var loggers = new NullLoggerFactory();
        var host = new Mock<IServerApplicationHost>().Object;
        var context = new Mock<IHttpContextAccessor>().Object;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var delayer = new Mock<IAsyncDelayer>().Object;
        var user = new UserConfig {
            UserApiAuth = [new UserApiAuth { Name = provider, AccessToken = "access", RefreshToken = "refresh" }],
            KeyPairs = [new KeyPairs { Key = "KitsuUserId", Value = "1" }, new KeyPairs { Key = "ShikimoriUserId", Value = "1" }]
        };
        IApiCallHelpers client = provider switch {
            ApiName.Mal => new ApiCallHelpers(malApiCalls: new MalApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.AniList => new ApiCallHelpers(aniListApiCalls: new AniListApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Kitsu => new ApiCallHelpers(kitsuApiCalls: new KitsuApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Annict => new ApiCallHelpers(annictApiCalls: new AnnictApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Shikimori => new ApiCallHelpers(shikimoriApiCalls: new ShikimoriApiCalls(http.Object, loggers, host, context, cache, delayer, new Dictionary<string, string> { { "User-Agent", "tests" } }, user)),
            ApiName.Simkl => new ApiCallHelpers(simklApiCalls: new SimklApiCalls(http.Object, loggers, host, context, cache, delayer, new Dictionary<string, string> { { "simkl-api-key", "client" } }, user)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        return new ApiCallHelpersTrackerProvider(provider, client, new FixedTime(Now));
    }

    private static ApiCallHelpersTrackerProvider Status(ApiName provider, HttpStatusCode status) =>
        Real(provider, () => new HttpResponseMessage(status));

    private static ApiCallHelpersTrackerProvider NoResponse(ApiName provider) =>
        Real(provider, () => throw new HttpRequestException("connection reset"));

    private static EntryState OnList(ApiName provider) =>
        new(Ref(provider), 12, new ListState(TrackerStatus.Watching, 3, false, 0), Now);

    private static readonly EntryChange Forward = new(5, TrackerStatus.Watching);

    private static IEnumerable<TestCaseData> Definite() {
        foreach (var provider in AllProviders) {
            yield return new TestCaseData(provider, HttpStatusCode.Unauthorized, TrackerFailureKind.Unauthorized);
            yield return new TestCaseData(provider, HttpStatusCode.TooManyRequests, TrackerFailureKind.RateLimited);
            yield return new TestCaseData(provider, HttpStatusCode.InternalServerError, TrackerFailureKind.Transient);
            yield return new TestCaseData(provider, HttpStatusCode.BadRequest, TrackerFailureKind.Rejected);
        }
    }

    [TestCaseSource(nameof(Definite))]
    public void AFailedReadIsTyped(ApiName provider, HttpStatusCode status, TrackerFailureKind expected) {
        var error = Assert.ThrowsAsync<TrackerFailureException>(() => Status(provider, status).ReadEntry(Ref(provider), CancellationToken.None));
        Assert.That(error!.Kind, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(Definite))]
    public void AFailedWriteIsTyped(ApiName provider, HttpStatusCode status, TrackerFailureKind expected) {
        var error = Assert.ThrowsAsync<TrackerFailureException>(() => Status(provider, status).Apply(OnList(provider), Forward, CancellationToken.None));
        Assert.That(error!.Kind, Is.EqualTo(expected));
    }

    [TestCaseSource(typeof(TrackerTestSupport), nameof(AllProviders))]
    public async Task ANotFoundReadIsStillNull(ApiName provider) =>
        Assert.That(await Status(provider, HttpStatusCode.NotFound).ReadEntry(Ref(provider), CancellationToken.None), Is.Null);

    [TestCaseSource(typeof(TrackerTestSupport), nameof(AllProviders))]
    public void AReadWithNoResponseIsTransient(ApiName provider) {
        var error = Assert.ThrowsAsync<TrackerFailureException>(() => NoResponse(provider).ReadEntry(Ref(provider), CancellationToken.None));
        Assert.That(error!.Kind, Is.EqualTo(TrackerFailureKind.Transient));
    }

    /// <summary>The request may have landed, so it stays Unconfirmed rather than Failed.</summary>
    [TestCaseSource(typeof(TrackerTestSupport), nameof(AllProviders))]
    public async Task AWriteWithNoResponseHasNoReceipt(ApiName provider) =>
        Assert.That(await NoResponse(provider).Apply(OnList(provider), Forward, CancellationToken.None), Is.Null);

    [Test]
    public void AFailedAniListSearchIsTyped() {
        var error = Assert.ThrowsAsync<TrackerFailureException>(() => Status(ApiName.AniList, HttpStatusCode.InternalServerError).Search("Fixture", CancellationToken.None));
        Assert.That(error!.Kind, Is.EqualTo(TrackerFailureKind.Transient));
    }

    [Test]
    public async Task APartialWriteKeepsItsReceiptAfterAReportedFailure() {
        var (adapter, fake) = Adapter(ApiName.AniList);
        fake.Add(SyncHarness.Entry(500, "Fixture Show", 12, null));
        fake.FailingWrites.Add(2);
        fake.WriteFailure = TrackerFailureKind.Transient;

        var receipt = await adapter.Apply(new EntryState(Ref(ApiName.AniList), 12, new ListState(TrackerStatus.Completed, 12, false, 1), Now),
            new EntryChange(1, TrackerStatus.Completed, RewatchTransition.Start), CancellationToken.None);

        Assert.That(receipt!.Complete, Is.False);
    }
}
