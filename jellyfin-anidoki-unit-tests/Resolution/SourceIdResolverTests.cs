using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Resolution;

/// <summary>A Mixed library's movie known only by TMDB, resolved through the anime list's AniDB ID.</summary>
[TestFixture, NonParallelizable]
public class SourceIdResolverTests {
    private const int AniDbId = 5975;
    private string _directory = null!;
    private AnimeListIndex _index = null!;
    private readonly List<Uri> _requests = new();

    private sealed class RecordingHandler(List<Uri> requests) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            requests.Add(request.RequestUri!);
            string body = JsonSerializer.Serialize(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse { Anilist = 21, MyAnimeList = 20, Kitsu = 11, AniDb = AniDbId });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-source-ids-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "anime-list-full.xml");
        File.WriteAllText(file, $"<anime-list><anime anidbid=\"{AniDbId}\" tvdbid=\"movie\" tmdbid=\"12429\" /></anime-list>");
        _index = new AnimeListIndex(() => file);
        _requests.Clear();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private async Task<SourceIds> Resolve(string tmdbId, bool idsOnly) {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new RecordingHandler(_requests)));
        var resolver = new SourceIdResolver(http.Object, new Mock<IApplicationPaths>().Object, NullLoggerFactory.Instance, NullLogger.Instance, _index);
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { ["Tmdb"] = tmdbId });
        return await resolver.Resolve(SyncSource.From(movie)! with { IdsOnly = idsOnly });
    }

    [Test]
    public async Task AMixedLibraryMovieKnownOnlyByTmdbResolvesThroughTheAnimeList() {
        SourceIds ids = await Resolve("12429", idsOnly: true);

        Assert.That((ids.Ids.Anilist, ids.Ids.MyAnimeList, ids.Ids.Kitsu, ids.Ids.AniDb), Is.EqualTo(((int?)21, (int?)20, (int?)11, (int?)AniDbId)));
        Assert.That(_requests, Has.Count.EqualTo(1));
        Assert.That(_requests[0].Query, Does.Contain("source=anidb").And.Contain($"id={AniDbId}"));
    }

    [Test]
    public async Task OutsideAMixedLibraryATmdbOnlyMovieResolvesAsBefore() {
        SourceIds ids = await Resolve("12429", idsOnly: false);

        Assert.That((ids.Ids.Anilist, ids.Ids.AniDb), Is.EqualTo(((int?)null, (int?)null)));
        Assert.That(_requests, Is.Empty);
    }

    [Test]
    public async Task AMovieTheAnimeListDoesNotKnowGetsNoIds() {
        SourceIds ids = await Resolve("603", idsOnly: true);

        Assert.That((ids.Ids.Anilist, ids.Ids.AniDb), Is.EqualTo(((int?)null, (int?)null)));
        Assert.That(_requests, Is.Empty);
    }
}
