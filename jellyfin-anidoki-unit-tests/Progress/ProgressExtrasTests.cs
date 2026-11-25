using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Matching;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Progress;

public class ProgressExtrasTests {
    [Test]
    public async Task AiringWorksWithoutALinkAndRefreshesAfterTheEpisodeOrTheCacheExpires() {
        using var fixture = new ProgressFixture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTime(DateTimeOffset.UnixEpoch);
        var sent = new List<Helpers.SentRequest>();
        var http = Helpers.RecordedSequence(sent,
            (HttpStatusCode.OK, """{"data":{"Media":{"nextAiringEpisode":{"episode":5,"airingAt":20}}}}"""),
            (HttpStatusCode.OK, """{"data":{"Media":{"nextAiringEpisode":null}}}"""),
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.OK, """{"data":{"Media":{"nextAiringEpisode":{"episode":6,"airingAt":7200}}}}"""));
        var airing = new AniListAiring(http, memory, clock, NullLogger<AniListAiring>.Instance);
        var details = new SeriesDetails(fixture.Items, Sources(fixture), fixture.Reader, fixture.Match.Trackers, airing, NullLogger<SeriesDetails>.Instance);
        var movie = new LibraryFixture().Movie("Film", new Dictionary<string, string> { ["AniList"] = "123" });
        var source = MatchApiFixture.Source(movie, [1]);

        Assert.That((await details.Airing(source, fixture.Match.Bob.Id, null, true, CancellationToken.None))?.Airing?.Episode, Is.EqualTo(5));
        Assert.That((await airing.Read(123, CancellationToken.None))?.Episode, Is.EqualTo(5));
        Assert.That(sent, Has.Count.EqualTo(1));

        clock.Now = DateTimeOffset.FromUnixTimeSeconds(21);
        Assert.That(await airing.Read(123, CancellationToken.None), Is.Null);
        Assert.That(await airing.Read(123, CancellationToken.None), Is.Null);
        Assert.That(sent, Has.Count.EqualTo(2));

        clock.Now = clock.Now.AddHours(1);
        Assert.That((await details.Airing(source, fixture.Match.Bob.Id, null, true, CancellationToken.None))?.Airing, Is.Null);
        Assert.That((await airing.Read(123, CancellationToken.None))?.Episode, Is.EqualTo(6));
        Assert.That(sent.All(request => !request.Headers.ContainsKey("Authorization")), Is.True);
    }

    [Test]
    public async Task RelationsValidateTheCourAndUseTheVisiblePersonalMatch() {
        using var fixture = new ProgressFixture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tracker = new Mock<ITrackerProvider>(MockBehavior.Strict);
        tracker.SetupGet(provider => provider.Name).Returns(ApiName.AniList);
        fixture.Match.Trackers.Link(fixture.Alice, tracker.Object);
        var first = new EntryRef(ApiName.AniList, 101, null);
        var second = new EntryRef(ApiName.AniList, 102, null);
        var related = new EntryRef(ApiName.AniList, 103, null);
        fixture.Shared(fixture.Match.Season, Range(1, 12, first), Range(13, 48, second));
        fixture.Shared(fixture.SeasonTwo, Range(1, 12, first));
        fixture.Personal(fixture.SeasonTwo, [Range(1, 12, related)]);
        fixture.Shared(fixture.Match.Hidden, Range(1, 12, related));
        var target = AddItem(fixture, fixture.SeasonTwo, false);
        AddItem(fixture, fixture.Match.Hidden, false);
        tracker.Setup(provider => provider.GetRelations(second, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new EntryRelation(RelationKind.Sequel, related, "The Sequel")]);

        var airing = new AniListAiring(new Mock<IHttpClientFactory>(MockBehavior.Strict).Object, memory, TimeProvider.System, NullLogger<AniListAiring>.Instance);
        var details = new SeriesDetails(fixture.Items, Sources(fixture), fixture.Reader, fixture.Match.Trackers, airing, NullLogger<SeriesDetails>.Instance);
        Assert.That(details.Select(fixture.Series.Id, fixture.Alice, null), Is.EqualTo(fixture.Match.Season));
        Assert.That(details.Select(fixture.Series.Id, fixture.Alice, fixture.SeasonTwo.Info.SourceKey), Is.EqualTo(fixture.SeasonTwo));
        Assert.That(details.Select(fixture.Series.Id, fixture.Alice, fixture.Match.Hidden.Info.SourceKey), Is.Null);
        Assert.That(await details.Related(fixture.Match.Season, fixture.Alice, ApiName.AniList, 999, true, CancellationToken.None), Is.Null);
        var result = await details.Related(fixture.Match.Season, fixture.Alice, ApiName.AniList, second.Id, true, CancellationToken.None);
        Assert.That(result?.Relations.Single().Title, Is.EqualTo("The Sequel"));
        Assert.That(result?.Relations.Single().LibraryMatches, Is.EqualTo(new[] { new RelatedLibraryItem(target.Id, 2) }));
        tracker.Verify(provider => provider.GetRelations(second, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheFirstVisibleMappingHonorsEpisodeExceptionsForRelationsAndAiring() {
        using var fixture = new ProgressFixture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var first = new EntryRef(ApiName.AniList, 101, null);
        var replacement = new EntryRef(ApiName.AniList, 102, null);
        var source = MatchApiFixture.Source(new LibraryFixture().Movie("Film"), [1]);
        fixture.Personal(source, [Range(1, 1, first)], [new TargetException(1, new MatchTarget(replacement, "Film"), 1)]);
        var tracker = new Mock<ITrackerProvider>(MockBehavior.Strict);
        tracker.SetupGet(provider => provider.Name).Returns(ApiName.AniList);
        tracker.Setup(provider => provider.GetRelations(replacement, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fixture.Match.Trackers.Link(fixture.Alice, tracker.Object);
        var sent = new List<Helpers.SentRequest>();
        var http = Helpers.RecordedSequence(sent, (HttpStatusCode.OK, """{"data":{"Media":{"nextAiringEpisode":null}}}"""));
        var airing = new AniListAiring(http, memory, TimeProvider.System, NullLogger<AniListAiring>.Instance);
        var details = new SeriesDetails(fixture.Items, Sources(fixture), fixture.Reader, fixture.Match.Trackers, airing, NullLogger<SeriesDetails>.Instance);
        Assert.That(await details.Related(source, fixture.Alice, ApiName.AniList, first.Id, true, CancellationToken.None), Is.Null);
        Assert.That((await details.Related(source, fixture.Alice, ApiName.AniList, null, true, CancellationToken.None))?.Relations, Is.Empty);
        Assert.That(await details.Airing(source, fixture.Alice, first.Id, true, CancellationToken.None), Is.Null);
        Assert.That(await details.Airing(source, fixture.Alice, null, true, CancellationToken.None), Is.Not.Null);
        using var body = System.Text.Json.JsonDocument.Parse(sent.Single().Body ?? throw new AssertionException("No GraphQL body"));
        Assert.That(body.RootElement.GetProperty("variables").GetProperty("id").GetInt32(), Is.EqualTo(replacement.Id));
    }

    [Test]
    public async Task LibraryJoinsMoviesAndFirstSegmentsAndCountsOnlyConfirmedAbsences() {
        using var fixture = new ProgressFixture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tracker = new Mock<ITrackerProvider>(MockBehavior.Strict);
        var list = tracker.As<ITrackerLibraryList>();
        tracker.SetupGet(provider => provider.Name).Returns(ApiName.AniList);
        fixture.Match.Trackers.Link(fixture.Alice, tracker.Object);
        var entry = new EntryRef(ApiName.AniList, 101, null);
        var second = new EntryRef(ApiName.AniList, 102, null);
        fixture.Shared(fixture.Match.Season, Range(1, 12, entry), Range(13, 48, second));
        var matched = AddItem(fixture, fixture.Match.Season, false);
        var movie = new LibraryFixture().Movie("Film", new Dictionary<string, string> { ["AniList"] = "333" });
        var movieItem = AddItem(fixture, fixture.Match.Library.Add(MatchApiFixture.Source(movie, [1])), true);
        var unmatched = AddItem(fixture, fixture.SeasonTwo, false);
        AddItem(fixture, fixture.Match.Hidden, false);
        for (int index = 0; index < 100; index++) {
            var extra = new LibraryFixture().Movie($"Unknown {index}");
            AddItem(fixture, fixture.Match.Library.Add(MatchApiFixture.Source(extra, [1])), true);
        }
        fixture.Match.WatchState.ByUser[fixture.Alice] = [1, 2];
        list.Setup(provider => provider.ReadLibraryList(It.IsAny<CancellationToken>())).ReturnsAsync([
            new TrackerLibraryEntry(entry, new ListState(TrackerStatus.Watching, 7, false, 0, 80), 12, true, DateTimeOffset.UtcNow.AddDays(1))
        ]);
        var reader = new LibraryProgress(Sources(fixture), fixture.Match.Trackers, new TrackerLibraryCache(memory, TimeProvider.System),
            TimeProvider.System, NullLogger<LibraryProgress>.Instance);
        var result = await reader.Read(fixture.Alice, null, false, true, CancellationToken.None);
        Assert.That(result?.Rows, Has.Count.EqualTo(103));
        var rows = result?.Rows.ToDictionary(row => row.ItemId);
        Assert.That(rows?[matched.Id].EntryId, Is.EqualTo(entry.Id));
        Assert.That(rows?[matched.Id].Score, Is.EqualTo(80));
        Assert.That(rows?[matched.Id].EpisodeCount, Is.EqualTo(12));
        Assert.That(rows?[movieItem.Id].EpisodeCount, Is.EqualTo(1));
        Assert.That(rows?[movieItem.Id].PlayedCount, Is.EqualTo(1));
        Assert.That(rows?[movieItem.Id].MatchState, Is.EqualTo(LibraryMatchState.NotOnList));
        Assert.That(rows?[unmatched.Id].MatchState, Is.EqualTo(LibraryMatchState.Unresolved));
        Assert.That(result?.NotOnListCount, Is.EqualTo(1));
        Assert.That((await reader.Read(fixture.Match.Bob.Id, null, false, true, CancellationToken.None))?.Rows, Is.Empty);
        list.Verify(provider => provider.ReadLibraryList(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task OverlappingRefreshesShareAReadAndCacheIsolatedByLinkExpires() {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTime(DateTimeOffset.UnixEpoch);
        var cache = new TrackerLibraryCache(memory, clock);
        var first = new TaskCompletionSource<IReadOnlyList<TrackerLibraryEntry>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new Mock<ITrackerLibraryList>(MockBehavior.Strict);
        tracker.SetupSequence(provider => provider.ReadLibraryList(It.IsAny<CancellationToken>()))
            .Returns(first.Task).ReturnsAsync((IReadOnlyList<TrackerLibraryEntry>?)null).ReturnsAsync([]).ReturnsAsync([]);
        var user = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var request = cache.Read(user, ApiName.Mal, generation, tracker.Object, true, CancellationToken.None);
        var concurrent = cache.Read(user, ApiName.Mal, generation, tracker.Object, true, CancellationToken.None);
        first.SetResult([]);
        await Task.WhenAll(request, concurrent);
        Assert.That(await cache.Read(user, ApiName.Mal, generation, tracker.Object, false, CancellationToken.None), Is.Empty);

        clock.Now = clock.Now.AddMinutes(10);
        Assert.That(await cache.Read(user, ApiName.Mal, generation, tracker.Object, false, CancellationToken.None), Is.Null);
        Assert.That(await cache.Read(user, ApiName.Mal, generation, tracker.Object, false, CancellationToken.None), Is.Empty);
        Assert.That(await cache.Read(user, ApiName.Mal, Guid.NewGuid(), tracker.Object, false, CancellationToken.None), Is.Empty);
        tracker.Verify(provider => provider.ReadLibraryList(It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    private static MatchRange Range(int first, int last, EntryRef entry) => new(first, last, new MatchTarget(entry, $"Entry {entry.Id}"), 1);

    private static SeriesItem AddItem(ProgressFixture fixture, LibrarySource source, bool movie) {
        var key = source.Info.SourceKey;
        var parsed = SourceKeys.Parse(key) ?? throw new InvalidOperationException("Invalid fixture source");
        return fixture.Items.Add(new SeriesItem(parsed.ItemId, source.Info.Title, movie, 2026, null, null, null, null, [], [key], null));
    }

    private static ProgressSources Sources(ProgressFixture fixture) {
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict).Object;
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(path => path.CachePath).Returns(fixture.Match.Directory);
        var arm = new ArmClient(http, NullLogger<ArmClient>.Instance, () => null);
        var animeList = new AnimeListSource(http, paths.Object, NullLogger<AnimeListSource>.Instance, () => null);
        return new ProgressSources(fixture.Match.Library, fixture.Items, fixture.Match.WatchState, fixture.Match.Store, arm, animeList,
            NullLogger<ProgressSources>.Instance);
    }
}
