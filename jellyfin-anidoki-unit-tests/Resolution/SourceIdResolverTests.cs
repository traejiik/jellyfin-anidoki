using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki_unit_tests.Characterization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Resolution;

/// <summary>
/// A movie's AniDB ID through the anime list: a Mixed library's movie known only by TMDB, and a movie whose AniDB ID the
/// list ties to it, to another anime, or to nothing.
/// </summary>
[TestFixture, NonParallelizable]
public class SourceIdResolverTests {
    private const int AniDbId = 5975;
    private string _directory = null!;
    private PluginConfiguration _configuration = null!;
    private AnimeListIndex _index = null!;
    private readonly List<Uri> _requests = new();

    private sealed class RecordingHandler(List<Uri> requests) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            requests.Add(request.RequestUri!);
            string body = JsonSerializer.Serialize(new ArmClient.OfflineDatabaseResponse { Anilist = 21, MyAnimeList = 20, Kitsu = 11, AniDb = AniDbId });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-source-ids-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "anime-list-full.xml");
        File.WriteAllText(file, $"""
            <anime-list>
              <anime anidbid="{AniDbId}" tvdbid="movie" tmdbid="12429" />
              <anime anidbid="313" tvdbid="75534" defaulttvdbseason="0" imdbid="tt0114563" />
            </anime-list>
            """);
        _configuration = new PluginConfiguration { animeListSaveLocation = _directory };
        _index = new AnimeListIndex(() => file);
        _requests.Clear();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private Task<SourceIds> Resolve(string tmdbId, bool idsOnly) => Resolve(new Dictionary<string, string> { ["Tmdb"] = tmdbId }, idsOnly);

    private async Task<SourceIds> Resolve(Dictionary<string, string> providerIds, bool idsOnly) {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new RecordingHandler(_requests)));
        var resolver = new SourceIdResolver(IdServices.Arm(http.Object, _configuration), IdServices.AnimeList(http.Object, configuration: _configuration),
            NullLogger.Instance);
        var movie = new LibraryFixture().Movie("Fixture Movie", providerIds);
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

    [TestCase(true)]
    [TestCase(false)]
    public async Task AnAniDbIdTheListTiesToTheMovieIsLookedUp(bool idsOnly) {
        SourceIds ids = await Resolve(new Dictionary<string, string> { ["AniDB"] = "313", ["Imdb"] = "tt0114563" }, idsOnly);

        Assert.That(ids.Problem, Is.Null);
        Assert.That(_requests, Has.Count.EqualTo(1), "the entry's season-0 row no longer hides it");
        Assert.That(_requests[0].Query, Does.Contain("source=anidb").And.Contain("id=313"));
    }

    [Test]
    public async Task AnAniDbIdTheListGivesAnotherMovieIsReplacedByTheTmdbOne() {
        await Resolve(new Dictionary<string, string> { ["AniDB"] = "313", ["Tmdb"] = "12429" }, idsOnly: true);

        Assert.That(_requests, Has.Count.EqualTo(1));
        Assert.That(_requests[0].Query, Does.Contain($"id={AniDbId}"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AnAniDbIdThatBelongsToAnotherMovieIsNotUsed(bool idsOnly) {
        SourceIds ids = await Resolve(new Dictionary<string, string> { ["AniDB"] = "313", ["Tmdb"] = "603", ["Imdb"] = "tt0133093" }, idsOnly);

        Assert.That((ids.Ids.Anilist, ids.Ids.AniDb), Is.EqualTo(((int?)null, (int?)null)));
        Assert.That(ids.Problem, Is.EqualTo(SourceIds.AniDbConflict));
        Assert.That(_requests, Is.Empty);
    }

    [Test]
    public async Task AMixedLibraryIgnoresAnAniDbIdTheListDoesNotKnow() {
        SourceIds ids = await Resolve(new Dictionary<string, string> { ["AniDB"] = "14176", ["Tmdb"] = "38757" }, idsOnly: true);

        Assert.That((ids.Ids.Anilist, ids.Ids.AniDb, ids.Problem), Is.EqualTo(((int?)null, (int?)null, (string?)null)));
        Assert.That(_requests, Is.Empty);
    }

    [Test]
    public void ShokoAniDbIdsAreNotChecked() {
        var movie = new LibraryFixture().Movie("Fixture Movie",
            new Dictionary<string, string> { ["AniDB"] = "313", ["Shoko Series"] = "1", ["Tmdb"] = "603", ["Imdb"] = "tt0133093" });

        Assert.That(MovieAniDbCheck.For(movie, _index), Is.EqualTo(new MovieAniDbCheck(MovieAniDbVerdict.Trusted, 313)),
            "a Shoko movie keeps the anime-list lookup it had");
    }

    [Test]
    public void AMovieWithNoUsableAniDbIdIsNotChecked() {
        var movie = new LibraryFixture().Movie("Fixture Movie", new Dictionary<string, string> { ["AniDB"] = "a313", ["Tmdb"] = "603" });

        Assert.That(MovieAniDbCheck.For(movie, _index), Is.Null);
    }
}
