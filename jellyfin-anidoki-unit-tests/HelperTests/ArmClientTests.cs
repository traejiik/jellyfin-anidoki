using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki_unit_tests.LoggingTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

public class ArmClientTests {
    private IHttpClientFactory _httpClientFactory = null!;

    private void Setup(List<Helpers.HttpCall> httpCalls) {
        Helpers.MockHttpCalls(httpCalls, ref _httpClientFactory);
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler) {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    [Test]
    public async Task DeserializeMetadataCall() {
        Setup(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("ids"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new ArmClient.OfflineDatabaseResponse {
                    AniDb = 1,
                    Anilist = 1,
                    Kitsu = 1,
                    MyAnimeList = 1
                })
            }
        });

        ArmClient.OfflineDatabaseResponse? result = await IdServices.Arm(_httpClientFactory, new PluginConfiguration()).GetIds(1, ArmClient.Source.Myanimelist);
        Assert.That(result, Is.Not.Null);
        Assert.IsTrue(result!.AniDb == 1);
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

        await IdServices.Arm(Factory(new OneResponse(new HttpResponseMessage(status) { Content = content })), new PluginConfiguration())
            .GetIds(1, ArmClient.Source.Anilist);

        Assert.That(content.Disposed, Is.True);
    }

    /// <summary>The ARM server is read from the settings on every lookup, so a changed server applies to the next one.</summary>
    [Test]
    public async Task EachLookupUsesTheArmServerSetAtTheTime() {
        var requests = new List<Uri>();
        var configuration = new PluginConfiguration { armServerBaseUrl = "https://arm.one.test/" };
        var arm = new ArmClient(Factory(new Recording(requests)), NullLogger<ArmClient>.Instance, () => configuration);

        await arm.GetIds(1, ArmClient.Source.Anidb);
        configuration.armServerBaseUrl = "http://arm.two.test";
        await arm.GetIds(2, ArmClient.Source.Kitsu);

        Assert.That(requests.Select(uri => uri.ToString()), Is.EqualTo(new[] {
            "https://arm.one.test/api/v2/ids?source=anidb&id=1",
            "http://arm.two.test/api/v2/ids?source=kitsu&id=2"
        }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ftp://arm.example.test")]
    [TestCase("not a url")]
    public async Task WithoutAUsableArmServerThePublicOneIsAsked(string? setting) {
        var requests = new List<Uri>();
        var logs = new CapturedLogs();
        var arm = new ArmClient(Factory(new Recording(requests)), logs.For<ArmClient>(), () => new PluginConfiguration { armServerBaseUrl = setting! });

        await arm.GetIds(7, ArmClient.Source.Anilist);

        Assert.That(requests.Single().ToString(), Is.EqualTo("https://arm.haglund.dev/api/v2/ids?source=anilist&id=7"));
        Assert.That(logs.At(LogLevel.Warning), Has.Length.EqualTo(string.IsNullOrEmpty(setting) ? 0 : 1));
        Assert.That(logs.Entries.Select(entry => entry.Category).Distinct(), Is.EqualTo(new[] { typeof(ArmClient).FullName }));
    }

    private sealed class Recording(List<Uri> requests) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") });
        }
    }
}
