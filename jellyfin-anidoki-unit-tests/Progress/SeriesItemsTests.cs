using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Progress;

public class SeriesItemsTests {
    [Test]
    public void AnEpisodeASeasonAndTheSeriesAllLeadToTheSeries() {
        var fixture = new LibraryFixture();
        Episode episode = fixture.Episode("Frieren", 2, 5);

        Assert.That(JellyfinSeriesItems.Root(episode)?.Id, Is.EqualTo(episode.SeriesId));
        Assert.That(JellyfinSeriesItems.Root(episode.Season)?.Id, Is.EqualTo(episode.SeriesId));
        Assert.That(JellyfinSeriesItems.Root(episode.Series)?.Id, Is.EqualTo(episode.SeriesId));
    }

    [Test]
    public void AMovieIsItsOwnRoot() {
        var movie = new LibraryFixture().Movie("Your Name");

        Assert.That(JellyfinSeriesItems.Root(movie), Is.SameAs(movie));
        Assert.That(JellyfinSeriesItems.OpenedSeason(movie), Is.Null);
    }

    [Test]
    public void TheSeasonOpenedFromIsKnownOnlyForASeasonOrEpisode() {
        Episode episode = new LibraryFixture().Episode("Frieren", 2, 5);

        Assert.That(JellyfinSeriesItems.OpenedSeason(episode), Is.EqualTo(2));
        Assert.That(JellyfinSeriesItems.OpenedSeason(episode.Season), Is.EqualTo(2));
        Assert.That(JellyfinSeriesItems.OpenedSeason(episode.Series), Is.Null);
    }

    [Test]
    public void KeysMatchTheSourceKeysSyncUses() {
        Episode episode = new LibraryFixture().Episode("Frieren", 2, 5);
        var series = SourceKeys.Parse(JellyfinSeriesItems.SeasonKey(episode.SeriesId, 2));
        var movie = SourceKeys.Parse(JellyfinSeriesItems.MovieKey(episode.Id));

        Assert.That(series, Is.EqualTo((episode.SeriesId, (int?)2)));
        Assert.That(movie, Is.EqualTo((episode.Id, (int?)null)));
    }
}

public class ScoreFormatCacheTests {
    private sealed class CountingTracker(ApiName provider, ScoreFormat? format) : ITrackerProvider {
        public int FormatReads { get; private set; }

        public ApiName Name => provider;

        public ProviderCapabilities Capabilities => ProviderCapabilities.For(provider);

        public Task<ScoreFormat?> ReadScoreFormat(CancellationToken cancellationToken) {
            FormatReads++;
            return Task.FromResult(format);
        }

        public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private readonly Guid _user = Guid.NewGuid();

    [Test]
    public async Task TheFormatIsReadOncePerLink() {
        var cache = new ScoreFormatCache(new MemoryCache(new MemoryCacheOptions()));
        var tracker = new CountingTracker(ApiName.AniList, ScoreFormat.Point5);
        Guid generation = Guid.NewGuid();

        Assert.That(await cache.Get(_user, generation, tracker, CancellationToken.None), Is.EqualTo(ScoreFormat.Point5));
        Assert.That(await cache.Get(_user, generation, tracker, CancellationToken.None), Is.EqualTo(ScoreFormat.Point5));
        Assert.That(tracker.FormatReads, Is.EqualTo(1));
    }

    [Test]
    public async Task ANewSignInReadsTheFormatAgain() {
        var cache = new ScoreFormatCache(new MemoryCache(new MemoryCacheOptions()));
        var tracker = new CountingTracker(ApiName.AniList, ScoreFormat.Point100);

        await cache.Get(_user, Guid.NewGuid(), tracker, CancellationToken.None);
        await cache.Get(_user, Guid.NewGuid(), tracker, CancellationToken.None);

        Assert.That(tracker.FormatReads, Is.EqualTo(2));
    }

    [Test]
    public async Task ATrackerWithoutScoresIsNeverAsked() {
        var cache = new ScoreFormatCache(new MemoryCache(new MemoryCacheOptions()));
        var tracker = new CountingTracker(ApiName.Annict, null);

        Assert.That(await cache.Get(_user, Guid.NewGuid(), tracker, CancellationToken.None), Is.Null);
        Assert.That(tracker.FormatReads, Is.Zero);
    }

    [Test]
    public async Task AnUnknownFormatIsNotRemembered() {
        var cache = new ScoreFormatCache(new MemoryCache(new MemoryCacheOptions()));
        var tracker = new CountingTracker(ApiName.AniList, null);
        Guid generation = Guid.NewGuid();

        await cache.Get(_user, generation, tracker, CancellationToken.None);
        await cache.Get(_user, generation, tracker, CancellationToken.None);

        Assert.That(tracker.FormatReads, Is.EqualTo(2));
    }
}
