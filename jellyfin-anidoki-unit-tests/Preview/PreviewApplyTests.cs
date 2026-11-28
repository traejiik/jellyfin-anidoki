using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Matching;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Preview;

/// <summary>Applying selected preview rows through the real executor, against fake trackers.</summary>
[TestFixture, NonParallelizable]
public class PreviewApplyTests {
    private SyncHarness _harness = null!;
    private Guid _user;
    private MatchStore _matches = null!;
    private PreviewStore _rows = null!;
    private OperationStore _operations = null!;
    private Mock<IAsyncDelayer> _delayer = null!;
    private CancellationTokenSource _cancellation = null!;

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _harness.ConfigureUser(_user, false, false, ApiName.AniList, ApiName.Kitsu);
        _matches = new MatchStore(Plugin.Instance!.Database, TimeProvider.System);
        _rows = new PreviewStore(Plugin.Instance.Database, TimeProvider.System);
        _operations = new OperationStore(Plugin.Instance.Database, TimeProvider.System);
        _delayer = new Mock<IAsyncDelayer>();
        _delayer.Setup(delayer => delayer.Delay(It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
        _cancellation = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown() {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    /// <summary>An episode the apply can look up again, as Jellyfin's library would return it.</summary>
    private Episode Season(string series, int episode) {
        Episode item = _harness.Library.Episode(series, 1, episode);
        _harness.Library.Library.Setup(library => library.GetItemById(item.Id)).Returns(item);
        return item;
    }

    private void Entry(int id, MyListStatus status, ApiName provider = ApiName.AniList, int episodes = 12, string? title = null) {
        Anime entry = SyncHarness.Entry(id, title ?? $"Entry {id}", episodes, status);
        FakeTracker tracker = _harness.Tracker(provider);
        tracker.Add(entry);
        tracker.SearchResults.Add(entry);
    }

    /// <summary>A selectable row as the preview stores it: the entry read through the real adapter, titles dropped.</summary>
    private async Task<PreviewRow> Row(int seq, Episode episode, ApiName provider, int entryId, int progress, string fingerprint,
        PreviewRowKind kind = PreviewRowKind.Change) {
        EntryState read = (await new TrackerProvider(provider, _harness.Tracker(provider), TimeProvider.System)
            .ReadEntry(new EntryRef(provider, entryId, null), CancellationToken.None))!;
        return new PreviewRow(seq, PreviewDirection.ToTrackers, provider, SyncSource.From(episode)!.SourceKey, episode.Id,
            $"{episode.SeriesName} · season 1", episode.IndexNumber, entryId.ToString(CultureInfo.InvariantCulture), $"Entry {entryId}", progress,
            kind, kind == PreviewRowKind.Reduction ? ReductionCheck.Reason : "progress", ResolvedBy.TitleSearch, null, null,
            _harness.Configuration.applySavedMatches, fingerprint,
            new EntryChange(progress, read.List!.Status), read with { Titles = null }, true, kind == PreviewRowKind.Change, null);
    }

    private OperationContext NewContext(OperationKind kind = OperationKind.SyncToTrackers) {
        Guid id = Guid.NewGuid();
        _operations.Create(id, _user, kind, "x");
        return new OperationContext(id, _user, _operations, NullLogger.Instance, _cancellation.Token);
    }

    private PreviewApply Applier(ISeasonWatchState watch, IReadOnlyList<PreviewRow> rows, IAsyncDelayer? delayer = null) =>
        new(_harness.Library.Library.Object, NullLoggerFactory.Instance, new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object,
            new HttpContextAccessor(), new MemoryCache(new MemoryCacheOptions()), delayer ?? _delayer.Object, _matches, watch, _user, rows) {
            ClientOverride = (provider, _) => _harness.Tracker(provider)
        };

    private async Task<OperationContext> Apply(ISeasonWatchState watch, params PreviewRow[] rows) {
        OperationContext context = NewContext();
        await Applier(watch, rows).Run(context);
        return context;
    }

    private static (OutcomeKind Kind, string Step) Only(OperationContext context) {
        ProviderWriteOutcome outcome = context.Outcomes.Single();
        return (outcome.Kind, outcome.Step);
    }

    [Test]
    public async Task ARowFromARealPreviewIsWrittenOnceAsPlanned() {
        Entry(600, Watching(3), title: "Fixture Show");
        Episode episode = Season("Fixture Show", 5);
        var watch = new FixedWatchState(1, 2, 3, 4, 5);
        OperationContext preview = NewContext(OperationKind.PreviewToTrackers);
        await new OutboundPreview(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(),
            IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()), _delayer.Object, _matches, _rows, watch, _user) {
            ClientOverride = (provider, _) => _harness.Tracker(provider),
            OnlyProvider = ApiName.AniList
        }.PreviewEpisodes([episode], preview);
        PreviewRow row = _rows.Page(preview.OperationId, null, null, 0, PreviewStore.MaxPageSize).Rows.Single(candidate => candidate.Selectable);

        OperationContext context = await Apply(watch, row);

        RecordedWrite write = _harness.Tracker(ApiName.AniList).Writes.Single();
        ProviderWriteOutcome outcome = context.Outcomes.Single();
        Assert.Multiple(() => {
            Assert.That((write.EntryId, write.Progress, write.Status), Is.EqualTo((600, 5, Status.Watching)));
            Assert.That((outcome.Kind, outcome.Step, outcome.TargetTitle), Is.EqualTo((OutcomeKind.Confirmed, "progress", "Fixture Show")));
            Assert.That(_rows.Outcomes(context.OperationId)[row.Seq], Is.EqualTo(new AppliedOutcome(OutcomeKind.Confirmed, "progress")));
            Assert.That(context.Message, Is.EqualTo("1 updated"));
        });
    }

    [Test]
    public async Task ARowWhoseTrackerEntryMovedIsRefusedAndNotWritten() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        PreviewRow row = await Row(0, episode, ApiName.AniList, 600, 5, "1-5");
        Entry(600, Watching(4));

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
        Assert.That(context.Message, Is.EqualTo("1 changed since the preview"));
    }

    [Test]
    public async Task ARowWhoseWatchedEpisodesChangedIsRefusedWithoutReadingTheTracker() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        PreviewRow row = await Row(0, episode, ApiName.AniList, 600, 5, "1-5");
        int reads = _harness.Tracker(ApiName.AniList).Reads.Count;

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5, 6), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Reads, Has.Count.EqualTo(reads));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
    }

    [Test]
    public async Task ARowPreviewedUnderAnotherSavedMatchIsRefused() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        // The row says a match at revision 4 was in force; there is none now.
        PreviewRow row = (await Row(0, episode, ApiName.AniList, 600, 5, "1-5")) with { MatchRevision = 4 };

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
    }

    [Test]
    public async Task ARowPreviewedUnderTheOtherSavedMatchSettingIsRefused() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        PreviewRow row = await Row(0, episode, ApiName.AniList, 600, 5, "1-5");
        _harness.Configuration.applySavedMatches = !row.ApplySavedMatches;

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
    }

    [Test]
    public async Task ARowForATrackerNoLongerLinkedIsRefused() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        PreviewRow row = (await Row(0, episode, ApiName.AniList, 600, 5, "1-5")) with { Provider = ApiName.Shikimori };

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
    }

    [Test]
    public async Task ARowWhoseSeasonIsGoneFromTheLibraryIsRefused() {
        Entry(600, Watching(3));
        Episode episode = Season("Fixture Show", 5);
        PreviewRow row = (await Row(0, episode, ApiName.AniList, 600, 5, "1-5")) with { SourceItem = Guid.NewGuid() };

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), row);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
    }

    [Test]
    public async Task AReductionWritesTheLowerProgressAndPlaybackDoesNotLowerItAgain() {
        Entry(600, Watching(14), episodes: 24, title: "Fixture Show");
        Episode episode = Season("Fixture Show", 8);
        PreviewRow row = await Row(0, episode, ApiName.AniList, 600, 8, "1-8", PreviewRowKind.Reduction);

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5, 6, 7, 8), row);
        await _harness.Run(_harness.NewUpdater(), Season("Fixture Show", 6), _user, SyncUpdateSource.Playback, ApiName.AniList);

        RecordedWrite write = _harness.Tracker(ApiName.AniList).Writes.Single();
        Assert.That((write.Progress, write.Status), Is.EqualTo((8, Status.Watching)));
        Assert.That(context.Outcomes.Single().Step, Is.EqualTo(ReductionCheck.Reason));
    }

    [Test]
    public async Task CancellingAfterTwoOfFiveRowsRecordsTwoWrittenAndThreeCancelled() {
        var rows = new List<PreviewRow>();
        string[] series = ["Fixture Show", "Harbor Lights", "Iron Garden", "Moon Courier", "Paper Comet"];
        for (int index = 0; index < series.Length; index++) {
            Entry(601 + index, Watching(1));
            rows.Add(await Row(index, Season(series[index], 2), ApiName.AniList, 601 + index, 2, "1-2"));
        }
        // The wait before the third row is where the admin's cancel lands.
        int waits = 0;
        var delayer = new Mock<IAsyncDelayer>();
        delayer.Setup(d => d.Delay(It.IsAny<TimeSpan>())).Callback(() => {
            if (++waits == 2)
                _cancellation.Cancel();
        }).Returns(Task.CompletedTask);
        OperationContext context = NewContext();

        Assert.ThrowsAsync<OperationCanceledException>(() => Applier(new FixedWatchState(1, 2), rows, delayer.Object).Run(context));

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Has.Count.EqualTo(2));
        Assert.That(context.Outcomes.Select(outcome => outcome.Step),
            Is.EqualTo(new[] { "progress", "progress", PreviewApply.Cancelled, PreviewApply.Cancelled, PreviewApply.Cancelled }));
        Assert.That(context.Message, Is.EqualTo("Stopped after 2 of 5. Updates already sent were kept."));
    }

    [Test]
    public async Task ARateLimitedTrackerStopsOnlyItsOwnRemainingRows() {
        Entry(600, Watching(3));
        Entry(600, Watching(3), ApiName.Kitsu);
        Entry(700, Watching(1));
        Episode first = Season("Fixture Show", 5);
        Episode second = Season("Harbor Lights", 5);
        PreviewRow[] rows = [
            await Row(0, first, ApiName.AniList, 600, 5, "1-5"),
            await Row(1, first, ApiName.Kitsu, 600, 5, "1-5"),
            await Row(2, second, ApiName.AniList, 700, 5, "1-5")
        ];
        _harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.RateLimited;

        OperationContext context = await Apply(new FixedWatchState(1, 2, 3, 4, 5), rows);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Has.Count.EqualTo(1));
        Assert.That(context.Outcomes.Select(outcome => (outcome.Provider, outcome.Kind, outcome.Step)), Is.EqualTo(new[] {
            (ApiName.AniList, OutcomeKind.Failed, "rate-limited"),
            (ApiName.Kitsu, OutcomeKind.Confirmed, "progress"),
            (ApiName.AniList, OutcomeKind.Skipped, PreviewApply.ProviderStopped)
        }));
        Assert.That(context.Message, Is.EqualTo("1 updated · 1 not tried · 1 failed"));
    }
}
