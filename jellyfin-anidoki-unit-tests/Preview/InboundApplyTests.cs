using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Preview;

/// <summary>Marking selected seasons of a preview from trackers, against the inbound harness.</summary>
[TestFixture, NonParallelizable]
public class InboundApplyTests {
    private InboundHarness _harness = null!;
    private PreviewStore _rows = null!;
    private OperationStore _operations = null!;
    private CancellationTokenSource _cancellation = null!;

    [SetUp]
    public void SetUp() {
        _harness = new InboundHarness();
        _harness.Delayer.Setup(delayer => delayer.Delay(It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
        _rows = new PreviewStore(Plugin.Instance!.Database, TimeProvider.System);
        _operations = new OperationStore(Plugin.Instance.Database, TimeProvider.System);
        _cancellation = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown() {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    private OperationContext NewContext(OperationKind kind) {
        Guid id = Guid.NewGuid();
        _operations.Create(id, _harness.UserId, kind, "x");
        return new OperationContext(id, _harness.UserId, _operations, NullLogger.Instance, _cancellation.Token);
    }

    /// <summary>A series whose first season maps to the AniList entry with the same ID.</summary>
    private Series Show(string name, int aniListId, int episodes = 4) {
        int aniDbId = 7000 + aniListId;
        _harness.Map(aniDbId, 9000 + aniListId, "1");
        _harness.Ids("anilist", aniListId, aniDbId);
        return _harness.Series(name, aniListId, (1, episodes));
    }

    /// <summary>The selectable rows of a real preview from AniList, in planned order.</summary>
    private async Task<IReadOnlyList<PreviewRow>> Preview() {
        OperationContext context = NewContext(OperationKind.PreviewFromTrackers);
        await new InboundPreview(_harness.NewSync(), _rows, _harness.Users.Object, _harness.UserData.Object, ApiName.AniList, _harness.UserId)
            .Run(context);
        return _rows.Page(context.OperationId, null, null, 0, PreviewStore.MaxPageSize).Rows.Where(row => row.Selectable).OrderBy(row => row.Seq).ToList();
    }

    private InboundApply Applier(IReadOnlyList<PreviewRow> rows) =>
        new(_harness.NewSync(), _harness.Library.Object, _harness.Users.Object, _harness.UserData.Object, NullLoggerFactory.Instance,
            _harness.UserId, rows);

    private async Task<OperationContext> Apply(params PreviewRow[] rows) {
        OperationContext context = NewContext(OperationKind.SyncFromTrackers);
        await Applier(rows).Run(context);
        return context;
    }

    private static (OutcomeKind Kind, string Step) Only(OperationContext context) {
        ProviderWriteOutcome outcome = context.Outcomes.Single();
        return (outcome.Kind, outcome.Step);
    }

    /// <summary>The notes the apply stored with its actions, in order.</summary>
    private static IReadOnlyList<string?> Notes(OperationContext context) {
        using var connection = Plugin.Instance!.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resolution_note FROM operation_actions WHERE operation_id = $id ORDER BY seq";
        command.Parameters.AddWithValue("$id", context.OperationId.ToString("D"));
        using var reader = command.ExecuteReader();
        var notes = new List<string?>();
        while (reader.Read())
            notes.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return notes;
    }

    [Test]
    public async Task ARowFromAPreviewIsMarkedOnceAsPlanned() {
        Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();

        OperationContext context = await Apply(row);

        ProviderWriteOutcome outcome = context.Outcomes.Single();
        Assert.Multiple(() => {
            Assert.That(_harness.Saves, Is.EqualTo(new[] {
                "Alpha S01E01 played 2026-01-02T00:00:00", "Alpha S01E02 played 2026-01-02T00:00:00", "Alpha S01E03 played 2026-01-02T00:00:00",
                "Alpha S01E04 played 2026-01-02T00:00:00", "Alpha season 1 played 2026-01-02T00:00:00"
            }));
            Assert.That((outcome.Provider, outcome.TargetId, outcome.TargetTitle), Is.EqualTo((ApiName.AniList, "1001", "Alpha")));
            Assert.That((outcome.Kind, outcome.Step, outcome.ConfirmedProgress, outcome.MeaningfulChange),
                Is.EqualTo((OutcomeKind.Confirmed, "mark-played", (int?)4, true)));
            Assert.That(_rows.Outcomes(context.OperationId)[row.Seq], Is.EqualTo(new AppliedOutcome(OutcomeKind.Confirmed, "mark-played")));
            Assert.That(OperationStates.FromOutcomes(context.Outcomes), Is.EqualTo(OperationState.Succeeded));
            Assert.That(context.Message, Is.EqualTo("1 marked played"));
        });
    }

    [Test]
    public async Task OnlyTheSelectedSeasonIsMarked() {
        Show("Alpha", 1001);
        Show("Beta", 1002, 2);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        _harness.Entry(1002, "Beta", Status.Completed, 2, "2026-01-03");
        IReadOnlyList<PreviewRow> rows = await Preview();
        Assert.That(rows, Has.Count.EqualTo(2));

        await Apply(rows.Single(row => row.TargetId == "1002"));

        Assert.That(_harness.Saves, Is.EqualTo(new[] {
            "Beta S01E01 played 2026-01-03T00:00:00", "Beta S01E02 played 2026-01-03T00:00:00", "Beta season 1 played 2026-01-03T00:00:00"
        }));
    }

    [TestCase("progress")]
    [TestCase("status")]
    [TestCase("finish-date")]
    [TestCase("gone")]
    public async Task ARowWhoseTrackerEntryChangedIsRefusedAndNothingIsMarked(string change) {
        Show("Alpha", 1001);
        Anime entry = _harness.Entry(1001, "Alpha", Status.Watching, 2);
        PreviewRow row = (await Preview()).Single();
        switch (change) {
            case "progress":
                entry.MyListStatus!.NumEpisodesWatched = 3;
                break;
            case "status":
                _harness.Tracker.Lists[Status.Watching].Remove(entry);
                _harness.Tracker.Lists[Status.Completed].Add(entry);
                entry.MyListStatus!.Status = Status.Completed;
                break;
            case "finish-date":
                entry.MyListStatus!.FinishDate = "2026-01-03";
                break;
            case "gone":
                _harness.Tracker.Lists[Status.Watching].Remove(entry);
                break;
        }

        OperationContext context = await Apply(row);

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
        Assert.That(Notes(context), Is.EqualTo(new[] { "tracker" }));
        Assert.That(context.Message, Is.EqualTo("1 changed since the preview"));
    }

    [Test]
    public async Task ARenamedTrackerEntryIsStillMarked() {
        Show("Alpha", 1001);
        Anime entry = _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();
        entry.Title = "Alpha (TV)";

        OperationContext context = await Apply(row);

        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Confirmed, "mark-played")));
    }

    [Test]
    public async Task ARowWhoseSeasonHadAnEpisodePlayedMeanwhileIsRefused() {
        Series series = Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();
        _harness.Played(InboundHarness.EpisodeOf(series, 1, 1));

        OperationContext context = await Apply(row);

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.Stale)));
        Assert.That(Notes(context), Is.EqualTo(new[] { "watched" }));
    }

    [Test]
    public async Task TheSeasonIsLoadedAfreshNotTakenFromJellyfinsItemCache() {
        // Jellyfin's cached season keeps the episodes, and their played state, it was first loaded with. Found on jellytest:
        // an apply through the cache refused unchanged seasons as watched, and could have marked changed ones.
        Series series = Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        _harness.Played(InboundHarness.EpisodeOf(series, 1, 1));
        PreviewRow row = (await Preview()).Single();
        Assert.That(row.WatchedFingerprint, Is.EqualTo("1"));
        // The cached copy has not seen its episodes yet, so through it the season would look unplayed.
        var cached = new Season { Id = row.SourceItem, IndexNumber = 1, SeriesName = "Alpha", Children = new List<BaseItem>() };
        _harness.Library.Setup(library => library.GetItemById(row.SourceItem)).Returns(cached);

        OperationContext context = await Apply(row);

        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Confirmed, "mark-played")));
        Assert.That(_harness.Saves, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task ARowWhoseSeasonGainedTheIgnoreTagIsRefused() {
        Series series = Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();
        InboundHarness.Ignore(InboundHarness.SeasonOf(series, 1));

        OperationContext context = await Apply(row);

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(Notes(context), Is.EqualTo(new[] { "ignored" }));
    }

    [Test]
    public async Task ARowWhoseSeasonIsGoneFromTheLibraryIsRefused() {
        Series series = Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();
        Guid season = InboundHarness.SeasonOf(series, 1).Id;
        _harness.Library.Setup(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.ItemIds.Contains(season))))
            .Returns(new List<BaseItem>());

        OperationContext context = await Apply(row);

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(Notes(context), Is.EqualTo(new[] { "item-missing" }));
    }

    [Test]
    public async Task EveryRowIsRefusedWithoutReadingTheListWhenTheTrackerIsNoLongerLinked() {
        Show("Alpha", 1001);
        Show("Beta", 1002, 2);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        _harness.Entry(1002, "Beta", Status.Completed, 2, "2026-01-03");
        IReadOnlyList<PreviewRow> rows = await Preview();
        Plugin.Instance!.PluginConfiguration.UserConfig.Single().UserApiAuth = [];
        // Reading the list now would record provider-stopped instead.
        _harness.Tracker.NoAnswer = true;

        OperationContext context = await Apply(rows.ToArray());

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(context.Outcomes.Select(outcome => outcome.Step), Is.EqualTo(new[] { PreviewApply.Stale, PreviewApply.Stale }));
        Assert.That(Notes(context), Is.EqualTo(new[] { "not-linked", "not-linked" }));
    }

    [Test]
    public async Task AnUnreadableListStopsEveryRowAndFailsTheRun() {
        Show("Alpha", 1001);
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        PreviewRow row = (await Preview()).Single();
        _harness.Tracker.NoAnswer = true;

        OperationContext context = await Apply(row);

        Assert.That(_harness.Saves, Is.Empty);
        Assert.That(Only(context), Is.EqualTo((OutcomeKind.Skipped, PreviewApply.ProviderStopped)));
        Assert.That(context.Result, Is.EqualTo(OperationState.Failed));
        Assert.That(context.Message, Is.EqualTo("The tracker list could not be read, so nothing was marked."));
    }

    [Test]
    public async Task CancellingDuringTheSecondOfThreeRowsMarksTwoAndRecordsOneCancelled() {
        Show("Alpha", 1001, 1);
        Show("Beta", 1002, 1);
        Show("Gamma", 1003, 1);
        _harness.Entry(1001, "Alpha", Status.Completed, 1, "2026-01-02");
        _harness.Entry(1002, "Beta", Status.Completed, 1, "2026-01-02");
        _harness.Entry(1003, "Gamma", Status.Completed, 1, "2026-01-02");
        IReadOnlyList<PreviewRow> rows = await Preview();
        // The admin's cancel lands while the second season is being marked; that season is still finished.
        var second = (Season)_harness.Library.Object.GetItemById(rows[1].SourceItem)!;
        _harness.Library.Setup(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.ItemIds.Contains(second.Id))))
            .Callback(() => _cancellation.Cancel()).Returns(new List<BaseItem> { second });
        OperationContext context = NewContext(OperationKind.SyncFromTrackers);

        Assert.ThrowsAsync<OperationCanceledException>(() => Applier(rows).Run(context));

        Assert.That(_harness.Saves.Count(save => save.Contains("season 1", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(context.Outcomes.Select(outcome => outcome.Step),
            Is.EqualTo(new[] { "mark-played", "mark-played", PreviewApply.Cancelled }));
        Assert.That(context.Message, Is.EqualTo("Stopped after 2 of 3. Updates already sent were kept."));
    }
}
