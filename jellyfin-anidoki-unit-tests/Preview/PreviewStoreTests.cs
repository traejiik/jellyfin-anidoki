using System;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Operations;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Preview;

[TestFixture]
public class PreviewStoreTests {
    private string _directory = null!;
    private AniDokiDatabase _database = null!;
    private MutableTime _clock = null!;
    private OperationStore _operations = null!;
    private PreviewStore _store = null!;
    private Guid _user;

    [SetUp]
    public void SetUp() {
        _directory = OperationTestSupport.NewDirectory();
        _database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _clock = new MutableTime(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        _operations = new OperationStore(_database, _clock);
        _store = new PreviewStore(_database, _clock);
        _user = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private static PreviewRow Row(int seq, string title = "Show · season 1", PreviewRowKind kind = PreviewRowKind.Change,
        ApiName provider = ApiName.AniList) =>
        new(seq, PreviewDirection.ToTrackers, provider, $"season:{title}", Guid.Parse("00000000-0000-0000-0000-000000000001"), title, 5,
            "500", "Entry 500", 5, kind, "progress", ResolvedBy.Ids, null, 3, true, "1-5", new EntryChange(5, TrackerStatus.Watching),
            new EntryState(new EntryRef(provider, 500, null), 12, new ListState(TrackerStatus.Watching, 3, false, 0), DateTimeOffset.UnixEpoch),
            kind is PreviewRowKind.Change or PreviewRowKind.Reduction, kind == PreviewRowKind.Change, null);

    /// <summary>A preview operation that ran and finished a minute after the previous one.</summary>
    private Guid Finished(OperationState state = OperationState.Succeeded, Guid? user = null) {
        _clock.Now = _clock.Now.AddMinutes(1);
        Guid id = Guid.NewGuid();
        _operations.Create(id, user ?? _user, OperationKind.PreviewToTrackers, "x");
        _operations.MarkRunning(id);
        _operations.Finish(id, state, null);
        return id;
    }

    private bool Applicable(Guid id) => _store.IsApplicable(_operations.Read(id)!);

    [Test]
    public void ARowReadsBackAsWritten() {
        Guid id = Finished();
        PreviewRow row = Row(0);

        _store.Add(id, row);

        Assert.That(_store.Page(id, null, null, 0, 10).Rows.Single(), Is.EqualTo(row));
    }

    [Test]
    public void RowsAreOrderedBySeasonTitleThenTracker() {
        Guid id = Finished();
        _store.Add(id, Row(0, "Zeta · season 1"));
        _store.Add(id, Row(1, "alpha · season 1", provider: ApiName.Kitsu));
        _store.Add(id, Row(2, "alpha · season 1"));

        Assert.That(_store.Page(id, null, null, 0, 10).Rows.Select(row => row.Seq), Is.EqualTo(new[] { 2, 1, 0 }));
    }

    [Test]
    public void KindAndTrackerFilterTheRowsAndTheTotal() {
        Guid id = Finished();
        _store.Add(id, Row(0));
        _store.Add(id, Row(1, kind: PreviewRowKind.Reduction));
        _store.Add(id, Row(2, provider: ApiName.Kitsu));

        Assert.That(_store.Page(id, PreviewRowKind.Change, null, 0, 10).Total, Is.EqualTo(2));
        Assert.That(_store.Page(id, null, ApiName.Kitsu, 0, 10).Total, Is.EqualTo(1));
        Assert.That(_store.Page(id, PreviewRowKind.Reduction, ApiName.Kitsu, 0, 10).Total, Is.Zero);
    }

    [Test]
    public void APageKeepsTheWholeTotal() {
        Guid id = Finished();
        foreach (int seq in Enumerable.Range(0, 5))
            _store.Add(id, Row(seq));

        PreviewPage page = _store.Page(id, null, null, 2, 2);

        Assert.That(page.Total, Is.EqualTo(5));
        Assert.That(page.Rows.Select(row => row.Seq), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public void CountsAreByKind() {
        Guid id = Finished();
        _store.Add(id, Row(0));
        _store.Add(id, Row(1));
        _store.Add(id, Row(2, kind: PreviewRowKind.Failed));

        Assert.That(_store.Counts(id), Is.EqualTo(new System.Collections.Generic.Dictionary<string, int> { ["Change"] = 2, ["Failed"] = 1 }));
    }

    [Test]
    public void AnInboundRowKeepsItsMarkAndTrackerEntry() {
        Guid id = Finished();
        Guid season = Guid.NewGuid();
        Guid[] episodes = [Guid.NewGuid(), Guid.NewGuid()];
        var row = new PreviewRow(0, PreviewDirection.FromTrackers, ApiName.AniList, "season:abc:1", season, "Show · season 1", 2, "1001", "Show", 2,
            PreviewRowKind.Change, "mark-played", null, null, null, false, "", null, null, true, true, null,
            new InboundChange(season, episodes, new DateTime(2026, 1, 2), true),
            new InboundEntry(ApiName.AniList, 1001, "Show", jellyfin_anidoki.Models.Mal.Status.Completed, 2, "2026-01-02"));

        _store.Add(id, row);

        PreviewRow read = _store.Page(id, null, null, 0, 10).Rows.Single();
        Assert.That(read.PlannedChange, Is.Null);
        Assert.That(read.PlannedAgainst, Is.Null);
        Assert.That(read.PlannedMark!.Episodes, Is.EqualTo(episodes));
        Assert.That(read.PlannedMark with { Episodes = episodes }, Is.EqualTo(row.PlannedMark! with { Episodes = episodes }));
        Assert.That(read.TrackerEntry, Is.EqualTo(row.TrackerEntry));
    }

    [Test]
    public void TheNewestFinishedPreviewIsApplicable() =>
        Assert.That(Applicable(Finished()), Is.True);

    [TestCase(OperationState.Cancelled)]
    [TestCase(OperationState.Interrupted)]
    [TestCase(OperationState.Partial)]
    public void APreviewThatDidNotFinishCleanlyIsNotApplicable(OperationState state) =>
        Assert.That(Applicable(Finished(state)), Is.False);

    [Test]
    public void APreviewOlderThanADayIsNotApplicable() {
        Guid id = Finished();
        _clock.Now = _clock.Now.AddHours(25);

        Assert.That(Applicable(id), Is.False);
    }

    [Test]
    public void ANewerPreviewReplacesAnOlderOne() {
        Guid older = Finished();
        Guid newer = Finished(OperationState.Cancelled);

        Assert.That(Applicable(older), Is.False);
        Assert.That(Applicable(newer), Is.False);
    }

    [Test]
    public void AnotherUsersPreviewDoesNotReplaceIt() {
        Guid mine = Finished();
        Finished(user: Guid.NewGuid());

        Assert.That(Applicable(mine), Is.True);
    }

    [Test]
    public void AnAppliedPreviewIsNotApplicable() {
        Guid id = Finished();
        _store.Add(id, Row(0));
        using (var connection = _database.Open()) {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE preview_rows SET applied_operation_id = 'apply'";
            command.ExecuteNonQuery();
        }

        Assert.That(Applicable(id), Is.False);
    }

    [Test]
    public void RetentionDeletesRowsWithTheirOperation() {
        Guid id = Finished();
        _store.Add(id, Row(0));
        _clock.Now = _clock.Now.AddDays(31);

        _operations.DeleteFinishedBefore(OperationRunner.Retention);

        Assert.That(_store.Page(id, null, null, 0, 10).Total, Is.Zero);
    }
}
