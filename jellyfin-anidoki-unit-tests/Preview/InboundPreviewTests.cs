using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki_unit_tests.Characterization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Preview;

/// <summary>The inbound preview against the inbound harness, with a strict user-data mock: saving anything throws.</summary>
[TestFixture, NonParallelizable]
public class InboundPreviewTests {
    private InboundHarness _harness = null!;
    private PreviewStore _rows = null!;
    private OperationStore _operations = null!;
    private CancellationTokenSource _cancellation = null!;

    [SetUp]
    public void SetUp() {
        _harness = new InboundHarness(strictUserData: true);
        _rows = new PreviewStore(Plugin.Instance!.Database, TimeProvider.System);
        _operations = new OperationStore(Plugin.Instance.Database, TimeProvider.System);
        _cancellation = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown() {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    private OperationContext NewContext() {
        Guid id = Guid.NewGuid();
        _operations.Create(id, _harness.UserId, OperationKind.PreviewFromTrackers, "x");
        return new OperationContext(id, _harness.UserId, _operations, NullLogger.Instance, _cancellation.Token);
    }

    private InboundPreview NewPreview() =>
        new(_harness.NewSync(), _rows, _harness.Users.Object, _harness.UserData.Object, ApiName.AniList, _harness.UserId);

    private async Task<(OperationContext Context, IReadOnlyList<PreviewRow> Rows)> RunPreview() {
        OperationContext context = NewContext();
        await NewPreview().Run(context);
        return (context, _rows.Page(context.OperationId, null, null, 0, PreviewStore.MaxPageSize).Rows);
    }

    private void AssertNothingSaved() =>
        _harness.UserData.Verify(manager => manager.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);

    private MediaBrowser.Controller.Entities.TV.Series Show(int episodes = 4) {
        _harness.Map(7001, 9001, "1");
        _harness.Ids("anilist", 1001, 7001);
        return _harness.Series("Alpha", 1001, (1, episodes));
    }

    [Test]
    public async Task ASeasonToMarkIsASelectedChangeAndNothingIsSaved() {
        var series = Show();
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");

        var (context, rows) = await RunPreview();

        AssertNothingSaved();
        PreviewRow row = rows.Single();
        var season = InboundHarness.SeasonOf(series, 1);
        Assert.Multiple(() => {
            Assert.That(row.Direction, Is.EqualTo(PreviewDirection.FromTrackers));
            Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Change));
            Assert.That(row.Reason, Is.EqualTo("mark-played"));
            Assert.That(row.SourceKey, Is.EqualTo($"season:{series.Id:N}:1"));
            Assert.That(row.SourceItem, Is.EqualTo(season.Id));
            Assert.That(row.SourceTitle, Is.EqualTo("Alpha · season 1"));
            Assert.That(row.SourceEpisode, Is.EqualTo(4));
            Assert.That(row.TargetId, Is.EqualTo("1001"));
            Assert.That(row.TargetEpisode, Is.EqualTo(4));
            Assert.That(row.WatchedFingerprint, Is.EqualTo(""));
            Assert.That(row.PlannedMark!.Episodes, Is.EqualTo(season.Children.Select(episode => episode.Id)));
            Assert.That(row.PlannedMark.MarksSeason, Is.True);
            Assert.That(row.PlannedMark.PlayedDate, Is.EqualTo(new DateTime(2026, 1, 2)));
            Assert.That(row.TrackerEntry, Is.EqualTo(new InboundEntry(ApiName.AniList, 1001, "Alpha", Status.Completed, 4, "2026-01-02")));
            Assert.That(row.Selectable && row.SelectedByDefault, Is.True);
            Assert.That(context.Result, Is.EqualTo(OperationState.Succeeded));
            Assert.That(context.Message, Is.EqualTo("Previewed 1 tracker entry: 1 season to mark as played. Nothing was changed."));
        });
    }

    [Test]
    public async Task APlayedSeasonIsNoChange() {
        var series = Show(2);
        foreach (BaseItem item in InboundHarness.SeasonOf(series, 1).Children.Append(InboundHarness.SeasonOf(series, 1)))
            _harness.Played(item);
        _harness.Entry(1001, "Alpha", Status.Completed, 2, "2026-01-02");

        var (_, rows) = await RunPreview();

        Assert.That(rows.Single().Kind, Is.EqualTo(PreviewRowKind.NoChange));
        Assert.That(rows.Single().Reason, Is.EqualTo("already-played"));
        Assert.That(rows.Single().WatchedFingerprint, Is.EqualTo("1-2"));
        Assert.That(rows.Single().Selectable, Is.False);
    }

    [Test]
    public async Task AnIgnoredSeasonIsSkipped() {
        InboundHarness.Ignore(InboundHarness.SeasonOf(Show(), 1));
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");

        var (_, rows) = await RunPreview();

        Assert.That(rows.Single().Kind, Is.EqualTo(PreviewRowKind.Skipped));
        Assert.That(rows.Single().Reason, Is.EqualTo("ignored"));
    }

    [Test]
    public async Task AnIgnoredEpisodeLeavesTheSeasonUnmarked() {
        InboundHarness.Ignore(InboundHarness.EpisodeOf(Show(3), 1, 2));
        _harness.Entry(1001, "Alpha", Status.Completed, 3, "2026-01-02");

        var (_, rows) = await RunPreview();

        PreviewRow row = rows.Single();
        Assert.That(row.Kind, Is.EqualTo(PreviewRowKind.Change));
        Assert.That(row.PlannedMark!.Episodes, Has.Count.EqualTo(2));
        Assert.That(row.PlannedMark.MarksSeason, Is.False);
        Assert.That(row.ResolutionNote, Is.EqualTo("1 episode carries the ignore tag, so the season itself is not marked"));
    }

    [Test]
    public async Task AnEntryWithoutAnAniDbIdIsUnresolved() {
        Show();
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        _harness.Entry(2002, "Unknown", Status.Watching, 1);

        var (_, rows) = await RunPreview();

        PreviewRow unresolved = rows.Single(row => row.Kind == PreviewRowKind.Unresolved);
        Assert.That(unresolved.Reason, Is.EqualTo("no-anidb-id"));
        Assert.That(unresolved.TargetId, Is.EqualTo("2002"));
        Assert.That(unresolved.SourceKey, Is.EqualTo(""));
        Assert.That(unresolved.TrackerEntry!.Status, Is.EqualTo(Status.Watching));
        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task NoListFromTheTrackerIsNoChange() {
        _harness.Tracker.NoAnswer = true;

        var (context, rows) = await RunPreview();

        Assert.That(rows, Is.Empty);
        Assert.That(context.Result, Is.EqualTo(OperationState.NoChange));
    }

    [Test]
    public void CancellingStopsBeforeAnyRow() {
        Show();
        _harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        _harness.Entry(1002, "Beta", Status.Completed, 4, "2026-01-02");
        _harness.Delayer.Setup(delayer => delayer.Delay(It.IsAny<TimeSpan>())).Callback(() => _cancellation.Cancel()).Returns(Task.CompletedTask);
        OperationContext context = NewContext();

        Assert.ThrowsAsync<OperationCanceledException>(() => NewPreview().Run(context));

        Assert.That(_rows.Page(context.OperationId, null, null, 0, 10).Total, Is.Zero);
        Assert.That(context.Message, Is.EqualTo("Cancelled. Nothing was changed, and this preview can no longer be applied."));
    }
}
