using System;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Simkl;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

/// <summary>D7: a user's recent updates, with each retry folded into the update it followed, and each tracker's last update.</summary>
[TestFixture]
public class HistoryReaderTests {
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private MutableTime _clock = null!;
    private AniDokiDatabase _database = null!;
    private OperationStore _store = null!;
    private HistoryReader _history = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _clock = new MutableTime(Start);
        _database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _store = new OperationStore(_database, _clock);
        _history = new HistoryReader(_database, _clock);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private static ProviderWriteOutcome Result(OutcomeKind kind, string step = "progress", ApiName provider = ApiName.AniList, bool meaningful = true) =>
        new(provider, "500", "Fixture Show", kind, null, null, false, step, kind == OutcomeKind.Confirmed && meaningful);

    /// <summary>Records one finished operation, ten minutes after the last, and returns its ID.</summary>
    private Guid Ran(OperationKind kind, params ProviderWriteOutcome[] results) =>
        Ran(kind, "Fixture Show · episode 5", null, null, results);

    private Guid Ran(OperationKind kind, string source, int? attempt, Guid? retryOf, params ProviderWriteOutcome[] results) {
        _clock.Now += TimeSpan.FromMinutes(10);
        Guid id = Guid.NewGuid();
        _store.Create(id, _user, kind, source, retryAttempt: attempt, retryOf: retryOf);
        for (int index = 0; index < results.Length; index++)
            _store.AddAction(id, index + 1, results[index]);
        _store.Finish(id, OperationStates.FromOutcomes(results), null);
        return id;
    }

    private Guid Retried(Guid previous, int attempt, params ProviderWriteOutcome[] results) =>
        Ran(OperationKind.Retry, "Fixture Show · episode 5", attempt, previous, results);

    private void Pending(ApiName provider, Guid lastOperation) {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retries (id, user_id, provider, source_item, series_key, trigger, attempt, due_utc, last_operation_id, last_step)
            VALUES ($id, $user, $provider, $item, 'season', 'Playback', 1, 'later', $operation, 'rate-limited')
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$user", _user.ToString("D"));
        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$item", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$operation", lastOperation.ToString("D"));
        command.ExecuteNonQuery();
    }

    private HistoryEntry Only() => _history.Read(_user, 0, 10).Entries.Single();

    private static string Line(HistoryEntry entry, ApiName provider = ApiName.AniList) =>
        entry.Results.Single(result => result.Provider == provider.ToString()).Result;

    // What is listed

    [Test]
    public void TheAdminsHistoryListsEveryUsersUpdatesWithTheirUser() {
        Guid mine = Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed));
        _clock.Now += TimeSpan.FromMinutes(10);
        Guid other = Guid.NewGuid(), theirs = Guid.NewGuid();
        _store.Create(theirs, other, OperationKind.Playback, "Other Show · episode 2");
        _store.AddAction(theirs, 1, Result(OutcomeKind.Confirmed));
        _store.Finish(theirs, OperationStates.FromOutcomes([Result(OutcomeKind.Confirmed)]), null);

        var everyone = _history.Read(null, 0, 10).Entries;
        Assert.That(everyone.Select(entry => (entry.OperationId, entry.UserId, entry.Title)),
            Is.EqualTo(new[] { (theirs, other, "Other Show"), (mine, _user, "Fixture Show") }), "newest first, each with its user");
        Assert.That(_history.Read(_user, 0, 10).Entries.Select(entry => entry.OperationId), Is.EqualTo(new[] { mine }), "one user's stays theirs");
        Assert.That(_history.Read(null, 0, 1), Has.Property(nameof(HistoryPage.Total)).EqualTo(2).And.Property(nameof(HistoryPage.More)).True, "the total counts every page");
    }

    [Test]
    public void NothingRecordedIsAnEmptyPage() {
        var page = _history.Read(_user, 0, 10);

        Assert.That(page.Entries, Is.Empty);
        Assert.That(page.More, Is.False);
    }

    [Test]
    public void PlaybackWatchedMarksConfirmedMarksAndEditsAreListedButManualSyncsAndPreviewsAreNot() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.WatchedToggle, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.ConfirmedManual, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.SyncToTrackers, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.SyncFromTrackers, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.PreviewToTrackers);
        Ran(OperationKind.ResolutionCheck);
        Ran(OperationKind.Edit, Result(OutcomeKind.Confirmed));

        Assert.That(_history.Read(_user, 0, 10).Entries.Select(entry => entry.Trigger),
            Is.EqualTo(new[] { "Edit", "ConfirmedManual", "WatchedToggle", "Playback" }));
    }

    [Test]
    public void AnUpdateStillRunningIsNotListed() {
        _store.Create(Guid.NewGuid(), _user, OperationKind.Playback, "Fixture Show · episode 6");

        Assert.That(_history.Read(_user, 0, 10).Entries, Is.Empty);
    }

    [Test]
    public void AnotherUsersUpdatesNeverAppear() {
        _store.Create(Guid.NewGuid(), Guid.NewGuid(), OperationKind.Playback, "Someone Else · episode 1");

        Assert.That(_history.Read(_user, 0, 10).Entries, Is.Empty);
    }

    [Test]
    public void UpdatesOlderThanRetentionAreNotListed() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed));
        _clock.Now += OperationRunner.Retention + TimeSpan.FromMinutes(1);

        Assert.That(_history.Read(_user, 0, 10).Entries, Is.Empty);
    }

    [Test]
    public void PagesRunNewestFirstUpToFifty() {
        for (int episode = 1; episode <= 55; episode++)
            Ran(OperationKind.Playback, $"Fixture Show · episode {episode}", null, null, Result(OutcomeKind.Confirmed));

        var first = _history.Read(_user, 0, 10);
        var last = _history.Read(_user, 40, 10);

        Assert.That(first.Entries.Select(entry => entry.Episode), Is.EqualTo(Enumerable.Range(46, 10).Reverse().Cast<int?>()));
        Assert.That(first.More, Is.True);
        Assert.That(last.Entries.Select(entry => entry.Episode), Is.EqualTo(Enumerable.Range(6, 10).Reverse().Cast<int?>()));
        Assert.That(last.More, Is.False);
    }

    [Test]
    public void TitleAndEpisodeComeFromTheSourceLabel() {
        Ran(OperationKind.Playback, "Frieren · episode 12", null, null, Result(OutcomeKind.Confirmed));
        Ran(OperationKind.Playback, "Your Name", null, null, Result(OutcomeKind.Confirmed));

        var entries = _history.Read(_user, 0, 10).Entries;

        Assert.That((entries[1].Title, entries[1].Episode), Is.EqualTo(("Frieren", (int?)12)));
        Assert.That((entries[0].Title, entries[0].Episode), Is.EqualTo(("Your Name", (int?)null)));
    }

    // Each tracker's line

    [Test]
    public void AnUpdateThatChangedNothingHasNoLines() {
        Ran(OperationKind.Playback, Result(OutcomeKind.NoChange, "already-watched"), Result(OutcomeKind.Confirmed, provider: ApiName.Kitsu, meaningful: false));

        Assert.That(Only().Results, Is.Empty);
    }

    [TestCase(OutcomeKind.Confirmed, "progress", "Updated")]
    [TestCase(OutcomeKind.Skipped, "plan-to-watch-only", "Skipped")]
    [TestCase(OutcomeKind.Unresolved, "resolve", "Unresolved")]
    [TestCase(OutcomeKind.Unconfirmed, "verify", "Unconfirmed")]
    [TestCase(OutcomeKind.Failed, "unauthorized", "NeedsSignIn")]
    [TestCase(OutcomeKind.Failed, "rejected", "Failed")]
    public void EachResultHasItsLine(OutcomeKind kind, string step, string expected) {
        Ran(OperationKind.Playback, Result(kind, step));

        Assert.That(Line(Only()), Is.EqualTo(expected));
    }

    [Test]
    public void ASkipKeepsThePlannersReason() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Skipped, "plan-to-watch-only"));

        Assert.That(Only().Results.Single().Step, Is.EqualTo("plan-to-watch-only"));
    }

    [Test]
    public void LinesFollowTheTrackerOrder() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed, provider: ApiName.Simkl), Result(OutcomeKind.Confirmed, provider: ApiName.AniList),
            Result(OutcomeKind.Confirmed, provider: ApiName.Kitsu));

        Assert.That(Only().Results.Select(result => result.Provider), Is.EqualTo(new[] { "AniList", "Kitsu", "Simkl" }));
    }

    [Test]
    public void OneTrackerWithSeveralEntriesShowsItsMostPressingResult() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed), Result(OutcomeKind.Failed, "unauthorized"));

        Assert.That(Line(Only()), Is.EqualTo("NeedsSignIn"));
    }

    // Retries

    [Test]
    public void AFailureWithAPendingRetryIsRetrying() {
        Guid original = Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "rate-limited"));
        Pending(ApiName.AniList, original);

        Assert.That(Line(Only()), Is.EqualTo("Retrying"));
    }

    [Test]
    public void ARetryIsFoldedIntoTheUpdateItFollowedAndCountsItsTry() {
        Guid original = Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "transient"), Result(OutcomeKind.Confirmed, provider: ApiName.Kitsu));
        Retried(original, 1, Result(OutcomeKind.Confirmed));

        var entry = Only();

        Assert.That(entry.OperationId, Is.EqualTo(original));
        Assert.That(entry.Trigger, Is.EqualTo("Playback"));
        Assert.That(entry.Results.Select(result => (result.Provider, result.Result, result.Try)),
            Is.EqualTo(new[] { ("AniList", "Updated", (int?)2), ("Kitsu", "Updated", (int?)null) }));
    }

    [Test]
    public void ARetryStillRunningKeepsTheLineRetrying() {
        Guid original = Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "rate-limited"));
        Guid running = Guid.NewGuid();
        _store.Create(running, _user, OperationKind.Retry, "Fixture Show · episode 5", retryAttempt: 1, retryOf: original);
        Pending(ApiName.AniList, running);

        Assert.That(Line(Only()), Is.EqualTo("Retrying"));
    }

    [Test]
    public void TheFourthFailedRetryGivesUp() {
        Guid previous = Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "rate-limited"));
        for (int attempt = 1; attempt <= RetryQueue.MaxAttempts; attempt++)
            previous = Retried(previous, attempt, Result(OutcomeKind.Failed, "rate-limited"));

        Assert.That(Line(Only()), Is.EqualTo("GaveUp"));
    }

    [Test]
    public void ARetryThatFoundNothingToChangeClearsTheLine() {
        Guid original = Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "transient"));
        Retried(original, 1, Result(OutcomeKind.NoChange, "already-watched"));

        Assert.That(Only().Results, Is.Empty);
    }

    [Test]
    public void AFailureWhoseRetryWasReplacedReadsFailed() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Failed, "rate-limited"));
        Guid newer = Ran(OperationKind.Playback, "Fixture Show · episode 6", null, null, Result(OutcomeKind.Failed, "rate-limited"));
        Pending(ApiName.AniList, newer);

        var entries = _history.Read(_user, 0, 10).Entries;

        Assert.That(entries.Select(entry => Line(entry)), Is.EqualTo(new[] { "Retrying", "Failed" }));
    }

    [Test]
    public void ARetryWhoseUpdateIsGoneShowsAsARetry() {
        Retried(Guid.NewGuid(), 2, Result(OutcomeKind.Confirmed));

        var entry = Only();

        Assert.That(entry.Trigger, Is.EqualTo("Retry"));
        Assert.That(entry.Results.Single().Try, Is.EqualTo(3));
    }

    // Last update

    [Test]
    public void LastUpdateIsTheNewestChangeEachTrackerConfirmed() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed));
        DateTimeOffset aniList = _clock.Now;
        Ran(OperationKind.SyncToTrackers, Result(OutcomeKind.Confirmed, provider: ApiName.Kitsu));
        DateTimeOffset kitsu = _clock.Now;
        Ran(OperationKind.Playback, Result(OutcomeKind.NoChange, "already-watched"), Result(OutcomeKind.Failed, "transient", ApiName.Kitsu));

        var activity = _history.Activity(_user, []).Single();

        Assert.That(activity.LastUpdateUtc, Is.EqualTo(kitsu));
        Assert.That(activity.Trackers.Select(tracker => (tracker.Provider, tracker.LastUpdateUtc)),
            Is.EqualTo(new[] { ("AniList", (DateTimeOffset?)aniList), ("Kitsu", (DateTimeOffset?)kitsu) }));
    }

    [Test]
    public void AUserWithNothingRecordedHasNoLastUpdate() {
        var activity = _history.Activity(_user, []).Single();

        Assert.That(activity.LastUpdateUtc, Is.Null);
        Assert.That(activity.Trackers, Is.Empty);
    }

    [Test]
    public void AttentionIsReportedWithTheTracker() {
        var attention = new Attention(_user, ApiName.Kitsu, AttentionKind.SignInRejected, Start, "unauthorized");

        var tracker = _history.Activity(_user, [attention]).Single().Trackers.Single();

        Assert.That(tracker.Provider, Is.EqualTo("Kitsu"));
        Assert.That(tracker.LastUpdateUtc, Is.Null);
        Assert.That(tracker.Attention, Is.EqualTo(new AttentionView("SignInRejected", Start, "unauthorized")));
    }

    [Test]
    public void EveryonesActivityListsEachUser() {
        Ran(OperationKind.Playback, Result(OutcomeKind.Confirmed));
        Guid other = Guid.NewGuid();
        _store.Create(Guid.NewGuid(), other, OperationKind.Playback, "x");

        Assert.That(_history.Activity(null, []).Select(activity => activity.UserId), Is.EqualTo(new[] { _user }));
    }
}
