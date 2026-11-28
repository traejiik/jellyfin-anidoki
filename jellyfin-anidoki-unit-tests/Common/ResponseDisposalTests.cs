using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
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
        var content = new TrackedContent(body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new OneResponse(new HttpResponseMessage(status) { Content = content })));

        await IdServices.AnimeList(factory.Object, configuration: new PluginConfiguration { animeListSaveLocation = Path.GetTempPath() }).Update();

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
