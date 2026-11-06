using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;
using Match = jellyfin_anidoki.Matching.Match;

namespace jellyfin_anidoki_unit_tests.Matching;

/// <summary>Saved matches through the whole pipeline: resolve, plan, write, record.</summary>
[TestFixture, NonParallelizable]
public class SavedMatchFlowTests {
    private SyncHarness _harness = null!;
    private Guid _user;
    private MatchStore _store = null!;
    private List<(ProviderWriteOutcome Outcome, OutcomeDetail? Detail)> _recorded = null!;
    private PlaybackOutcomeCollector _collector = null!;

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _harness.ConfigureUser(_user, false, false, ApiName.AniList);
        _harness.SeedAll(SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));
        FakeTracker tracker = _harness.Tracker(ApiName.AniList);
        foreach (int id in new[] { A, B })
            tracker.Add(SyncHarness.Entry(id, $"Entry {id}", 12, Watching(0)));
        _store = new MatchStore(Plugin.Instance!.Database, TimeProvider.System);
        _recorded = [];
        _collector = new PlaybackOutcomeCollector((outcome, detail) => _recorded.Add((outcome, detail)));
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private SyncSource SaveFor(MediaBrowser.Controller.Entities.TV.Episode episode, params MatchRange[] ranges) => SaveFor(episode, ranges, []);

    private SyncSource SaveFor(MediaBrowser.Controller.Entities.TV.Episode episode, MatchRange[] ranges, MatchException[] exceptions) {
        SyncSource source = SyncSource.From(episode)!;
        _store.Save(MatchSource.From(source), new Match(new MatchScope(ApiName.AniList, source.SourceKey), ranges, exceptions), 0, "admin");
        return source;
    }

    private Task Run(MediaBrowser.Controller.Entities.TV.Episode episode, SyncUpdateSource source = SyncUpdateSource.Playback, ISeasonWatchState? watch = null) {
        SyncPipeline pipeline = _harness.NewUpdater();
        pipeline.WatchState = watch;
        return pipeline.Run(episode, _user, true, _collector, source);
    }

    [Test]
    public async Task OffAutomaticIsWrittenAndTheSavedAnswerIsRecordedInTheNote() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, Range(1, 12, A));

        await Run(episode);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Single().EntryId, Is.EqualTo(500));
        OutcomeDetail detail = _recorded.Single().Detail!;
        Assert.That(detail.Note, Is.EqualTo("saved match would resolve to Entry 101 ep 5"));
        Assert.That(detail.MatchRevision, Is.Null);
    }

    [Test]
    public async Task OnTheSavedEntryIsWrittenAndItsRevisionRecorded() {
        _harness.Configuration.applySavedMatches = true;
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, Range(1, 12, A));

        await Run(episode);

        RecordedWrite write = _harness.Tracker(ApiName.AniList).Writes.Single();
        Assert.That(write.EntryId, Is.EqualTo(A));
        Assert.That(write.Progress, Is.EqualTo(5));
        OutcomeDetail detail = _recorded.Single().Detail!;
        Assert.That(detail.By, Is.EqualTo(ResolvedBy.SharedMatch));
        Assert.That(detail.MatchRevision, Is.EqualTo(1));
    }

    [Test]
    public async Task OnASeasonWithNoMatchIsWrittenAsBefore() {
        _harness.Configuration.applySavedMatches = true;

        await Run(_harness.Library.Episode("Fixture Show", 1, 5));

        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Single().EntryId, Is.EqualTo(500));
        Assert.That(_recorded.Single().Detail!.MatchRevision, Is.Null);
    }

    [Test]
    public async Task OnAnIgnoredEpisodeIsSkippedAndNothingIsWritten() {
        _harness.Configuration.applySavedMatches = true;
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        SaveFor(episode, [Range(1, 12, A)], [Skip(5)]);

        await Run(episode);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        var (outcome, detail) = _recorded.Single();
        Assert.That(outcome.Kind, Is.EqualTo(OutcomeKind.Skipped));
        Assert.That(outcome.Step, Is.EqualTo("match-ignored"));
        Assert.That(outcome.TargetTitle, Is.EqualTo("Fixture Show"));
        Assert.That(detail!.By, Is.EqualTo(ResolvedBy.SharedMatch));
        Assert.That(detail.MatchRevision, Is.EqualTo(1));
    }

    [Test]
    public async Task OnEarlierEntriesAreRecordedAsTheMatchNotTheSequelChain() {
        _harness.Configuration.applySavedMatches = true;
        var episode = _harness.Library.Episode("Fixture Show", 1, 20);
        SaveFor(episode, Range(1, 12, A), Range(13, 24, B));

        await Run(episode, watch: new FixedWatchState(1, 2, 12));

        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Select(write => (write.EntryId, write.Progress)), Is.EqualTo(new[] { (A, 12), (B, 8) }));
        Assert.That(_recorded.Select(entry => entry.Detail!.By), Is.All.EqualTo(ResolvedBy.SharedMatch));
    }

    [Test]
    public async Task OnPlayingAnyEpisodeOfACompletedEntryStartsARewatchAndDoesNotCountOne() {
        _harness.ConfigureUser(_user, false, true, ApiName.AniList);
        _harness.Configuration.applySavedMatches = true;
        _harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(A, "Entry 101", 12, new MyListStatus { Status = Status.Completed, NumEpisodesWatched = 12, RewatchCount = 4 }));
        var episode = _harness.Library.Episode("Fixture Show", 1, 2);
        SaveFor(episode, Range(1, 12, A));

        await Run(episode, watch: new FixedWatchState(1, 2, 12));

        // AniList's rewatch start takes two calls.
        var writes = _harness.Tracker(ApiName.AniList).Writes;
        Assert.That(writes.Select(write => write.Progress), Is.All.EqualTo(2));
        Assert.That(writes.Select(write => write.RewatchCount ?? 4), Is.All.EqualTo(4));
    }

    [Test]
    public async Task OnAManualSyncWritesEveryEntryWithSomethingWatched() {
        _harness.Configuration.applySavedMatches = true;
        var episode = _harness.Library.Episode("Fixture Show", 1, 12);
        SaveFor(episode, [Range(1, 12, A)], [SendTo(1, B, 1)]);

        await Run(episode, SyncUpdateSource.ManualSync, new FixedWatchState(1, 12));

        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Select(write => write.EntryId), Is.EquivalentTo(new[] { A, B }));
    }

    [Test]
    public async Task ManualSyncToTrackersUsesTheWholeSeasonAndItsWatchState() {
        string directory = OperationTestSupport.NewDirectory();
        try {
            _harness.Configuration.applySavedMatches = true;
            var episode = _harness.Library.Episode("Fixture Show", 1, 12);
            SaveFor(episode, [Range(1, 12, A)], [SendTo(1, B, 1)]);
            var operations = new OperationStore(new AniDokiDatabase(Path.Combine(directory, "anidoki.db")), TimeProvider.System);
            Guid id = Guid.NewGuid();
            operations.Create(id, _user, OperationKind.SyncToTrackers, "x");
            var context = new OperationContext(id, _user, operations, NullLogger.Instance, default);
            var sync = new SyncProviderFromLocal(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance,
                new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), new Mock<IApplicationPaths>().Object,
                new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, _user.ToString(), new FixedWatchState(1, 12)) {
                ClientOverride = (provider, _) => _harness.Tracker(provider)
            };

            await sync.SyncEpisodes([episode], context);

            Assert.That(_harness.Tracker(ApiName.AniList).Writes.Select(write => write.EntryId), Is.EquivalentTo(new[] { A, B }));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task TheRevisionReachesOperationHistory() {
        string directory = OperationTestSupport.NewDirectory();
        try {
            _harness.Configuration.applySavedMatches = true;
            var episode = _harness.Library.Episode("Fixture Show", 1, 5);
            SaveFor(episode, Range(1, 12, A));
            var database = new AniDokiDatabase(Path.Combine(directory, "anidoki.db"));
            var operations = new OperationStore(database, TimeProvider.System);
            Guid id = Guid.NewGuid();
            operations.Create(id, _user, OperationKind.Playback, "x");
            var context = new OperationContext(id, _user, operations, NullLogger.Instance, default);

            await _harness.NewUpdater().Run(episode, _user, true, new PlaybackOutcomeCollector(context.Record), SyncUpdateSource.Playback);

            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT match_revision, resolved_by FROM operation_actions";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetInt64(0), Is.EqualTo(1));
            Assert.That(reader.GetString(1), Is.EqualTo("SharedMatch"));
        } finally {
            Directory.Delete(directory, true);
        }
    }
}
