using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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
using jellyfin_anidoki.Models.Mal;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Records every request the real provider clients send. <see cref="FakeTracker"/> stands in for these clients
/// everywhere else, so this is what notices a status mapped differently or a call dropped when they change.
/// </summary>
[TestFixture, NonParallelizable]
public class ClientCharacterizationTests {
    private static readonly ApiName[] Providers = [ApiName.Mal, ApiName.AniList, ApiName.Kitsu, ApiName.Annict, ApiName.Shikimori, ApiName.Simkl];
    private static readonly ApiName[] ListProviders = [ApiName.Mal, ApiName.AniList, ApiName.Kitsu];
    private static readonly Status[] Statuses = [Status.Watching, Status.Completed, Status.On_hold, Status.Dropped, Status.Plan_to_watch, Status.Rewatching];
    private static readonly bool?[] Rewatching = [null, true, false];
    private static readonly DateTime Started = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Ended = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse Ids = new() { Anilist = 11, MyAnimeList = 12, Kitsu = 13, AniDb = 14 };

    private sealed class Recorder : HttpMessageHandler {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {request.RequestUri} {body}".TrimEnd());
            // Simkl only goes on to read the entry and the list once the ID lookup finds something.
            string response = request.RequestUri!.AbsolutePath.EndsWith("/search/id") ? "[{\"title\":\"Fixture\",\"ids\":{\"simkl\":9}}]" : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private static IApiCallHelpers Client(ApiName provider, Recorder recorder) {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(recorder, false));
        var loggers = new NullLoggerFactory();
        var host = new Mock<IServerApplicationHost>().Object;
        var context = new Mock<IHttpContextAccessor>().Object;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var delayer = new Mock<IAsyncDelayer>().Object;
        var user = new UserConfig {
            UserApiAuth = [new UserApiAuth { Name = provider, AccessToken = "access", RefreshToken = "refresh" }],
            KeyPairs = [new KeyPairs { Key = "KitsuUserId", Value = "1" }, new KeyPairs { Key = "ShikimoriUserId", Value = "1" }]
        };
        return provider switch {
            ApiName.Mal => new ApiCallHelpers(malApiCalls: new MalApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.AniList => new ApiCallHelpers(aniListApiCalls: new AniListApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Kitsu => new ApiCallHelpers(kitsuApiCalls: new KitsuApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Annict => new ApiCallHelpers(annictApiCalls: new AnnictApiCalls(http.Object, loggers, host, context, cache, delayer, user)),
            ApiName.Shikimori => new ApiCallHelpers(shikimoriApiCalls: new ShikimoriApiCalls(http.Object, loggers, host, context, cache, delayer, new Dictionary<string, string> { { "User-Agent", "tests" } }, user)),
            ApiName.Simkl => new ApiCallHelpers(simklApiCalls: new SimklApiCalls(http.Object, loggers, host, context, cache, delayer, new Dictionary<string, string> { { "simkl-api-key", "client" } }, user)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }

    private static async Task Record(StringBuilder output, Recorder recorder, string operation, Func<Task> call) {
        recorder.Requests.Clear();
        string? threw = null;
        try {
            await call();
        } catch (Exception error) {
            threw = error.GetType().Name;
        }

        output.Append(operation).Append('\n');
        foreach (string request in recorder.Requests)
            output.Append("  ").Append(request).Append('\n');
        if (threw != null)
            output.Append("  threw ").Append(threw).Append('\n');
    }

    [Test]
    public async Task EveryProviderSendsTheApprovedRequests() {
        // Two requests depend on server settings, read from Plugin.Instance, so fix them rather than inherit another test's.
        using var harness = new SyncHarness();
        harness.Configuration.updateNsfw = false;
        harness.Configuration.simklUpdateAll = false;
        var output = new StringBuilder();
        foreach (var provider in Providers) {
            output.Append("## ").Append(provider).Append('\n');
            var recorder = new Recorder();
            var client = Client(provider, recorder);
            await Record(output, recorder, "search", () => client.SearchAnime("Fixture"));
            await Record(output, recorder, "read", () => client.GetAnime(500, "alt-500"));
            await Record(output, recorder, "read related", () => client.GetAnime(500, "alt-500", getRelated: true));
            await Record(output, recorder, "read without alternative id", () => client.GetAnime(500));
            await Record(output, recorder, "read by cross ids", () => client.GetAnime(Ids, "Fixture"));
            foreach (var status in Statuses) {
                foreach (var rewatching in Rewatching) {
                    await Record(output, recorder, $"write {status} rewatching={rewatching?.ToString() ?? "-"}",
                        () => client.UpdateAnime(500, 5, status, rewatching, 3, Started, Ended, "alt-500", Ids, true));
                }
            }

            await Record(output, recorder, "write without identifiers", () => client.UpdateAnime(500, 5, Status.Watching));
        }

        foreach (var provider in ListProviders) {
            output.Append("## ").Append(provider).Append(" list\n");
            var recorder = new Recorder();
            var client = Client(provider, recorder);
            var source = client;
            await Record(output, recorder, "user", () => source.GetUser());
            foreach (var status in Statuses)
                await Record(output, recorder, $"list {status}", () => source.GetAnimeList(status, 7));
            await Record(output, recorder, "list without user", () => source.GetAnimeList(Status.Watching));
        }

        output.Append("## settings on\n");
        harness.Configuration.updateNsfw = true;
        harness.Configuration.simklUpdateAll = true;
        var malRecorder = new Recorder();
        await Record(output, malRecorder, "Mal search with NSFW", () => Client(ApiName.Mal, malRecorder).SearchAnime("Fixture"));
        var simklRecorder = new Recorder();
        await Record(output, simklRecorder, "Simkl write updating every episode",
            () => Client(ApiName.Simkl, simklRecorder).UpdateAnime(500, 3, Status.Watching, null, null, null, null, null, Ids, true));

        ApprovedFile.Verify("clients", output.ToString());
    }
}
