using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.AniList;
using jellyfin_anidoki.Trackers.Kitsu;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.API_tests;

public class WholeTrackerListTests {
    [TestCase(ApiName.Mal, false)]
    [TestCase(ApiName.Mal, true)]
    [TestCase(ApiName.AniList, false)]
    [TestCase(ApiName.AniList, true)]
    [TestCase(ApiName.Kitsu, false)]
    [TestCase(ApiName.Kitsu, true)]
    public async Task WholeListReadsEveryPageAndDiscardsResultsWhenALaterPageFails(ApiName provider, bool fail) {
        var sent = new List<Helpers.SentRequest>();
        var responses = new List<(HttpStatusCode Code, string Body)>();
        if (provider == ApiName.AniList)
            responses.Add((HttpStatusCode.OK, """{"data":{"Viewer":{"id":7}}}"""));
        if (provider == ApiName.Kitsu)
            responses.Add((HttpStatusCode.OK, """{"data":[{"id":"7","attributes":{"name":"Test"}}]}"""));
        responses.Add((HttpStatusCode.OK, Page(provider, 41, true)));
        responses.Add(fail ? (HttpStatusCode.InternalServerError, "{}") : (HttpStatusCode.OK, Page(provider, 42, false)));
        var http = Helpers.RecordedSequence(sent, responses.ToArray());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var host = Mock.Of<IServerApplicationHost>();
        var context = Mock.Of<IHttpContextAccessor>();
        var delayer = Mock.Of<IAsyncDelayer>();
        var config = new UserConfig { UserApiAuth = [new UserApiAuth { Name = provider, AccessToken = "test", RefreshToken = "test" }] };
        ITrackerClient client = provider switch {
            ApiName.Mal => new MalTrackerClient(new MalApiCalls(http, NullLoggerFactory.Instance, host, context, cache, delayer, config)),
            ApiName.AniList => new AniListTrackerClient(new AniListApiCalls(http, NullLoggerFactory.Instance, host, context, cache, delayer, config)),
            _ => new KitsuTrackerClient(new KitsuApiCalls(http, NullLoggerFactory.Instance, host, context, cache, delayer, config))
        };
        var tracker = new TrackerProvider(provider, client, TimeProvider.System);

        if (fail) {
            IReadOnlyList<TrackerLibraryEntry>? result = null;
            try {
                result = await tracker.ReadLibraryList(CancellationToken.None);
            } catch (TrackerFailureException error) {
                Assert.That(error.Kind, Is.EqualTo(TrackerFailureKind.Transient));
            }
            Assert.That(result, Is.Null);
        } else {
            var result = await tracker.ReadLibraryList(CancellationToken.None) ?? throw new AssertionException("The whole list was unreadable");
            Assert.That(result.Select(row => row.Entry.Id), Is.EqualTo(new[] { 41, 42 }));
            Assert.That(result.Select(row => row.List.Status), Is.EqualTo(new[] { TrackerStatus.PlanToWatch, TrackerStatus.OnHold }));
            Assert.That(result[1].List.Score, Is.EqualTo(provider == ApiName.AniList ? 85 : 80));
            Assert.That(result[1].TotalEpisodes, Is.EqualTo(12));
            Assert.That(result[1].IsAiring, Is.True);
            Assert.That(result[1].NextAiringAtUtc, Is.EqualTo(provider == ApiName.AniList ? DateTimeOffset.FromUnixTimeSeconds(2000000000) : (DateTimeOffset?)null));
        }
        Assert.That(sent, Has.Count.EqualTo(provider == ApiName.Mal ? 2 : 3));
        if (provider == ApiName.Kitsu)
            Assert.That(Uri.UnescapeDataString(sent[^1].Uri?.Query ?? ""), Does.Contain("page[offset]=2"));
        Assert.That(sent.All(request => request.Uri?.Query.Contains("filter[status]", StringComparison.OrdinalIgnoreCase) != true
            && request.Body?.Contains("status:", StringComparison.Ordinal) != true), Is.True);
    }

    private static string Page(ApiName provider, int id, bool hasNext) {
        string hasNextJson = hasNext ? "true" : "false";
        string malNext = hasNext ? """ ,"next":"https://api.myanimelist.net/v2/users/@me/animelist?offset=100" """ : "";
        string kitsuNext = hasNext ? """ "next":"https://kitsu.io/api/edge/library-entries?page[offset]=2" """ : "";
        string malStatus = hasNext ? "plan_to_watch" : "on_hold";
        string aniListStatus = hasNext ? "PLANNING" : "PAUSED";
        string kitsuStatus = hasNext ? "planned" : "on_hold";
        return provider switch {
            ApiName.Mal => $$$$$"""{"data":[{"node":{"id":{{{{{id}}}}},"num_episodes":12,"status":"currently_airing"},"list_status":{"status":"{{{{{malStatus}}}}}","score":8,"num_episodes_watched":4}}],"paging":{"previous":null{{{{{malNext}}}}}}}""",
            ApiName.AniList => $$$$$"""{"data":{"MediaListCollection":{"hasNextChunk":{{{{{hasNextJson}}}}},"lists":[{"entries":[{"status":"{{{{{aniListStatus}}}}}","progress":4,"score":85.0,"media":{"id":{{{{{id}}}}},"status":"RELEASING","episodes":12,"nextAiringEpisode":{"airingAt":2000000000}}}]}]}}}""",
            _ => $$$$$"""{"data":[{"attributes":{"status":"{{{{{kitsuStatus}}}}}","progress":4,"ratingTwenty":16},"relationships":{"anime":{"data":{"id":"{{{{{id}}}}}"}}}}],"included":[{"id":"{{{{{id}}}}}","attributes":{"episodeCount":12,"status":"current"}}],"links":{ {{{{{kitsuNext}}}}} }}"""
        };
    }
}
