using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Matching;
using jellyfin_anidoki_unit_tests.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Progress;

public class EntryEditorTests {
    private ProgressFixture _fixture = null!;
    private WritableTracker _tracker = null!;
    private EntryEditor _editor = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;

    [SetUp]
    public void SetUp() {
        _fixture = new ProgressFixture();
        _tracker = new WritableTracker(_fixture.Match.Catalogue);
        _fixture.Match.Trackers.Link(_fixture.Alice, _tracker);
        _fixture.Shared(_fixture.Match.Season, Range(1, 12, A), Range(13, 24, B), Range(25, 36, C), Range(37, 48, D));
        _fixture.Match.Catalogue.Add(A, 12, new ListState(TrackerStatus.Watching, 8, false, 0, 70));
        _editor = new EntryEditor(_fixture.Items, _fixture.Reader, _fixture.Match.Trackers, NullLogger<EntryEditor>.Instance);
        (_store, _runner) = OperationTestSupport.Create(_fixture.Match.Directory);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private static ShownStateContract Shown(TrackerStatus? status = TrackerStatus.Watching, int? progress = 8, int? score = 70) => new(status, progress, score);

    private static EntryEditContract Edit(int entry = A, ShownStateContract? shown = null, TrackerStatus status = TrackerStatus.Watching, int progress = 9,
        int? score = null, bool clear = false, ApiName provider = ApiName.AniList) => new(provider, entry, null, shown ?? Shown(), status, progress, score, clear);

    private Task<EditCheck> Check(params EntryEditContract[] edits) => Check(_fixture.Series.Id, edits);

    private Task<EditCheck> Check(Guid item, params EntryEditContract[] edits) =>
        _editor.Check(_fixture.Alice, new EditRequest(item, edits), true, CancellationToken.None);

    /// <summary>Checks and runs the edits as the controller does, and returns their results.</summary>
    private async Task<EditOutcomeContract> Save(params EntryEditContract[] edits) {
        var ready = (EditCheck.Ready)await Check(edits);
        OperationRunner.Handle handle = _runner.Enqueue(_fixture.Alice, OperationKind.Edit, ready.SeriesName, operation => _editor.Run(ready.Edits, operation))!;
        await handle.Completion;
        return EntryEditor.Results(_store.Read(handle.OperationId)!, _store.ReadActions(handle.OperationId));
    }

    [Test]
    public async Task AnEditThatStillMatchesTheTrackerIsSaved() {
        var outcome = await Save(Edit(progress: 10, score: 90));

        Assert.That(outcome.Finished, Is.True);
        Assert.That(outcome.Results.Single().Result, Is.EqualTo(EditResultKind.Saved));
        Assert.That(_tracker.Writes.Single().Change, Is.EqualTo(new EntryChange(10, TrackerStatus.Watching, Score: 90)));
    }

    [TestCase(TrackerStatus.Completed, 8, 70, TestName = "AChangedStatusIsStale")]
    [TestCase(TrackerStatus.Watching, 9, 70, TestName = "ChangedProgressIsStale")]
    [TestCase(TrackerStatus.Watching, 8, 60, TestName = "AChangedScoreIsStale")]
    public async Task AnEntryChangedOnTheTrackerIsNotWritten(TrackerStatus shownStatus, int shownProgress, int shownScore) {
        var outcome = await Save(Edit(shown: Shown(shownStatus, shownProgress, shownScore), progress: 11));

        EditResultContract result = outcome.Results.Single();
        Assert.That(result.Result, Is.EqualTo(EditResultKind.Stale));
        Assert.That(result.Current, Is.EqualTo(new ShownStateContract(TrackerStatus.Watching, 8, 70)));
        Assert.That(_tracker.Writes, Is.Empty);
    }

    [Test]
    public async Task LoweringProgressIsWrittenBecauseTheUserAskedForIt() {
        var outcome = await Save(Edit(progress: 3));

        Assert.That(outcome.Results.Single().Result, Is.EqualTo(EditResultKind.Saved));
        Assert.That(_fixture.Match.Catalogue.Entries[A].List!.Progress, Is.EqualTo(3));
    }

    [Test]
    public async Task ProgressPastAKnownTotalIsRefused() {
        Assert.That(await Check(Edit(progress: 13)), Is.TypeOf<EditCheck.Invalid>());
    }

    [Test]
    public async Task AnEntryBeingRewatchedIsNotEditedSoItsRewatchIsNotEnded() {
        _fixture.Match.Catalogue.Add(B, 12, new ListState(TrackerStatus.Completed, 1, true, 1));

        Assert.That(await Check(Edit(entry: B, shown: Shown(TrackerStatus.Completed, 1, null), status: TrackerStatus.Completed, progress: 1, score: 85)),
            Is.TypeOf<EditCheck.Invalid>());
    }

    [Test]
    public async Task AnUnknownTotalAcceptsAnyProgress() {
        _fixture.Match.Catalogue.Add(B, 0, new ListState(TrackerStatus.Watching, 2, false, 0));

        Assert.That(await Check(Edit(entry: B, shown: Shown(progress: 2, score: null), progress: 500)), Is.TypeOf<EditCheck.Ready>());
    }

    [Test]
    public async Task AnEntryOutsideTheUsersMatchesIsForbidden() {
        _fixture.Match.Catalogue.Add(999, 12, new ListState(TrackerStatus.Watching, 1, false, 0));

        Assert.That(await Check(Edit(entry: 999)), Is.TypeOf<EditCheck.Forbidden>());
    }

    [Test]
    public async Task ASeriesTheUserCannotSeeIsNotFound() {
        var hidden = _fixture.Items.Add(_fixture.Series with { Id = Guid.NewGuid() }, _fixture.Match.Bob.Id);

        Assert.That(await Check(hidden.Id, Edit()), Is.TypeOf<EditCheck.NotFound>());
    }

    [Test]
    public async Task AnUnlinkedTrackerIsRefused() {
        Assert.That(await Check(Edit(provider: ApiName.Kitsu)), Is.TypeOf<EditCheck.Invalid>());
    }

    [TestCase(0, false, TestName = "AScoreBelowTheScaleIsRefused")]
    [TestCase(101, false, TestName = "AScoreAboveTheScaleIsRefused")]
    [TestCase(50, true, TestName = "SettingAndClearingAScoreTogetherIsRefused")]
    public async Task ABadScoreIsRefused(int score, bool clear) {
        Assert.That(await Check(Edit(score: score, clear: clear)), Is.TypeOf<EditCheck.Invalid>());
    }

    [Test]
    public async Task ATrackerWithoutScoresSendsNoScore() {
        var annict = new CatalogueFake(ApiName.Annict);
        var writable = new WritableTracker(annict);
        _fixture.Match.Trackers.Link(_fixture.Alice, writable);
        var entry = new EntryRef(ApiName.Annict, 55, null);
        _fixture.Match.Automatic.Answer = new(new jellyfin_anidoki.Matching.Match(new jellyfin_anidoki.Matching.MatchScope(ApiName.Annict, _fixture.Match.Key),
            [new jellyfin_anidoki.Matching.MatchRange(1, 48, new jellyfin_anidoki.Matching.MatchTarget(entry, "Annict entry"), 1)], []), null);
        annict.Add(55, 48, new ListState(TrackerStatus.Watching, 1, false, null));

        var ready = (EditCheck.Ready)await Check(Edit(entry: 55, provider: ApiName.Annict, shown: Shown(progress: 1, score: 999), progress: 2, score: 80));
        await _editor.Edit(ready.Edits.Single(), NewOperation());

        Assert.That(writable.Writes.Single().Change.Score, Is.Null);
    }

    [TestCase(TrackerFailureKind.Unauthorized, "unauthorized")]
    [TestCase(TrackerFailureKind.RateLimited, "rate-limited")]
    [TestCase(TrackerFailureKind.Rejected, "rejected")]
    public async Task AFailedWriteSaysWhy(TrackerFailureKind kind, string failure) {
        _tracker.WriteFailure = kind;

        EditResultContract result = (await Save(Edit())).Results.Single();

        Assert.That(result.Result, Is.EqualTo(EditResultKind.Failed));
        Assert.That(result.Failure, Is.EqualTo(failure));
    }

    [Test]
    public async Task AWriteTheTrackerDidNotConfirmSaysSo() {
        _tracker.Unconfirmed = true;

        Assert.That((await Save(Edit())).Results.Single().Result, Is.EqualTo(EditResultKind.Unconfirmed));
    }

    [Test]
    public async Task EditsAppearInHistory() {
        await Save(Edit());

        var history = new HistoryReader(new jellyfin_anidoki.Storage.AniDokiDatabase(System.IO.Path.Combine(_fixture.Match.Directory, "anidoki.db")), TimeProvider.System);
        var entry = history.Read(_fixture.Alice, 0, 10).Entries.Single();
        Assert.That(entry.Trigger, Is.EqualTo("Edit"));
        Assert.That(entry.Title, Is.EqualTo("Long Show"));
        Assert.That(entry.Results.Single().Result, Is.EqualTo("Updated"));
    }

    private OperationContext NewOperation() {
        OperationContext? captured = null;
        _runner.Enqueue(_fixture.Alice, OperationKind.Edit, "x", operation => {
            captured = operation;
            return Task.CompletedTask;
        })!.Completion.GetAwaiter().GetResult();
        return captured!;
    }
}
