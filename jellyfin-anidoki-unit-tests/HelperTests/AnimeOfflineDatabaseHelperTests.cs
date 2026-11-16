using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

public class AnimeOfflineDatabaseHelperTests {
    private HttpClient _httpClient;
    private ILogger _logger = new NullLogger<AnimeOfflineDatabaseHelperTests>();

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        Helpers.MockHttpCalls(httpCalls, ref _httpClient);
    }

    [Test]
    public async Task DeserializeMetadataCall() {
        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("ids"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                    AniDb = 1,
                    Anilist = 1,
                    Kitsu = 1,
                    MyAnimeList = 1
                })
            }
        });

        AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? result = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClient, _logger, 1, AnimeOfflineDatabaseHelpers.Source.Myanimelist);
        Assert.IsTrue(result != null);
        Assert.IsTrue(result.AniDb == 1);
        Assert.IsTrue(result.Anilist == 1);
        Assert.IsTrue(result.Kitsu == 1);
        Assert.IsTrue(result.MyAnimeList == 1);
    }

    /// <summary>The ARM response, and the body read from it, are disposed whatever the answer was.</summary>
    [TestCase(HttpStatusCode.OK, "{\"anidb\":1,\"anilist\":2}")]
    [TestCase(HttpStatusCode.OK, "null")]
    [TestCase(HttpStatusCode.OK, "not json")]
    [TestCase(HttpStatusCode.NotFound, "")]
    public async Task TheArmResponseIsDisposed(HttpStatusCode status, string body) {
        var content = new TrackedContent(body);
        using var client = new HttpClient(new OneResponse(new HttpResponseMessage(status) { Content = content }));

        await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(client, _logger, 1, AnimeOfflineDatabaseHelpers.Source.Anilist);

        Assert.That(content.Disposed, Is.True);
    }
}
