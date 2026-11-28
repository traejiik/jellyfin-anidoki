using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki_unit_tests.Common;
using jellyfin_anidoki_unit_tests.Logging;
using jellyfin_anidoki_unit_tests.Operations;
using jellyfin_anidoki_unit_tests.Trackers.Kitsu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.AnimeIds;

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

    private const string Ids = "{\"anidb\":10,\"anilist\":20,\"myanimelist\":30,\"kitsu\":40}";

    private static ArmClient Cached(Scripted handler, TimeProvider? clock = null, PluginConfiguration? configuration = null, int maxEntries = 10_000) {
        PluginConfiguration settings = configuration ?? new PluginConfiguration();
        return new ArmClient(Factory(handler), NullLogger<ArmClient>.Instance, () => settings, clock, maxEntries);
    }

    [Test]
    public async Task ASecondLookupOfTheSameIdIsAnsweredFromTheCache() {
        var handler = new Scripted(Ids);
        ArmClient arm = Cached(handler);

        ArmClient.OfflineDatabaseResponse? first = await arm.GetIds(5, ArmClient.Source.Anilist);
        ArmClient.OfflineDatabaseResponse? second = await arm.GetIds(5, ArmClient.Source.Anilist);

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(second, Is.Not.Null);
        Assert.That((second!.AniDb, second.Anilist, second.MyAnimeList, second.Kitsu), Is.EqualTo((first!.AniDb, first.Anilist, first.MyAnimeList, first.Kitsu)));
    }

    [Test]
    public async Task AnotherIdSourceOrServerIsAskedAgain() {
        var handler = new Scripted(Ids);
        var configuration = new PluginConfiguration();
        ArmClient arm = Cached(handler, configuration: configuration);

        await arm.GetIds(5, ArmClient.Source.Anilist);
        await arm.GetIds(6, ArmClient.Source.Anilist);
        await arm.GetIds(5, ArmClient.Source.Kitsu);
        configuration.armServerBaseUrl = "https://arm.other.test";
        await arm.GetIds(5, ArmClient.Source.Anilist);

        Assert.That(handler.Requests.Select(uri => uri.ToString()), Is.EqualTo(new[] {
            "https://arm.haglund.dev/api/v2/ids?source=anilist&id=5",
            "https://arm.haglund.dev/api/v2/ids?source=anilist&id=6",
            "https://arm.haglund.dev/api/v2/ids?source=kitsu&id=5",
            "https://arm.other.test/api/v2/ids?source=anilist&id=5"
        }));
    }

    [Test]
    public async Task ACachedAnswerExpiresAfterAnHour() {
        var handler = new Scripted(Ids);
        var clock = new Operations.MutableTime(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        ArmClient arm = Cached(handler, clock);

        await arm.GetIds(5, ArmClient.Source.Anilist);
        clock.Now += TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1);
        await arm.GetIds(5, ArmClient.Source.Anilist);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));

        clock.Now += TimeSpan.FromSeconds(1);
        await arm.GetIds(5, ArmClient.Source.Anilist);
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    /// <summary>Fail-closed: a failed or empty answer is never kept, so the next lookup asks again and only a later success is cached.</summary>
    [TestCase("500")]
    [TestCase("throw")]
    [TestCase("not json")]
    [TestCase("null")]
    public async Task AFailedLookupIsNotCached(string failure) {
        var handler = new Scripted(failure, failure, Ids);
        ArmClient arm = Cached(handler);

        Assert.That(await arm.GetIds(5, ArmClient.Source.Anilist), Is.Null);
        Assert.That(await arm.GetIds(5, ArmClient.Source.Anilist), Is.Null);
        Assert.That(await arm.GetIds(5, ArmClient.Source.Anilist), Is.Not.Null);
        Assert.That((await arm.GetIds(5, ArmClient.Source.Anilist))?.AniDb, Is.EqualTo(10));

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ChangingAnAnswerDoesNotChangeTheCache() {
        ArmClient arm = Cached(new Scripted(Ids));

        ArmClient.OfflineDatabaseResponse? first = await arm.GetIds(5, ArmClient.Source.Anilist);
        first!.AniDb = 999;
        ArmClient.OfflineDatabaseResponse? second = await arm.GetIds(5, ArmClient.Source.Anilist);

        Assert.That(second!.AniDb, Is.EqualTo(10));
        Assert.That(second, Is.Not.SameAs(first));
    }

    [Test]
    public async Task TheCacheStaysWithinItsBound() {
        ArmClient arm = Cached(new Scripted(Ids), maxEntries: 2);

        for (int id = 1; id <= 5; id++)
            await arm.GetIds(id, ArmClient.Source.Anilist);

        Assert.That(arm.CachedCount, Is.LessThanOrEqualTo(2));
    }

    /// <summary>Answers each request with the next scripted reply, repeating the last: a body, "500", or "throw".</summary>
    private sealed class Scripted(params string[] replies) : HttpMessageHandler {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request.RequestUri!);
            string reply = replies[Math.Min(Requests.Count - 1, replies.Length - 1)];
            return reply switch {
                "throw" => throw new HttpRequestException("connection refused"),
                "500" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") }),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) })
            };
        }
    }

    private sealed class Recording(List<Uri> requests) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") });
        }
    }
}
