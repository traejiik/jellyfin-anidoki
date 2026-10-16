using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.API_tests;

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
}
