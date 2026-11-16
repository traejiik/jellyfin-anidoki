using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>The anime-list update and the GraphQL read dispose the response they read.</summary>
public class ResponseDisposalTests {
    [TestCase(HttpStatusCode.OK, "null")]
    [TestCase(HttpStatusCode.NotFound, "")]
    public async Task TheAnimeListUpdateDisposesTheRepositoryResponse(HttpStatusCode status, string body) {
        // The update reads where to keep the file from the plugin's settings.
        using var harness = new InboundHarness();
        var content = new TrackedContent(body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new OneResponse(new HttpResponseMessage(status) { Content = content })));

        await new UpdateAnimeList(factory.Object, NullLoggerFactory.Instance, new Mock<IApplicationPaths>().Object).Update();

        Assert.That(content.Disposed, Is.True);
    }

    [Test]
    public async Task TheGraphQlReadDisposesTheResponse() {
        var content = new TrackedContent("{}");
        using var client = new HttpClient(new OneResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await GraphQlHelper.DeserializeRequest<object>(client, NullLogger.Instance, ApiName.AniList, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, "query", null);

        Assert.That(content.Disposed, Is.True);
    }
}
