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

/// <summary>D6: which failures become retries, when each one is due, and when a retry goes away.</summary>
[TestFixture]
public class RetryQueueTests {
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private MutableTime _clock = null!;
    private RetryQueue _queue = null!;
    private DateTimeOffset? _rateLimitedUntil;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _episode5 = Guid.NewGuid();
    private readonly Guid _episode6 = Guid.NewGuid();
    private readonly Guid _otherSeason = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _clock = new MutableTime(Start);
        _rateLimitedUntil = null;
        _queue = Queue();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private RetryQueue Queue() => new(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), _clock,
        item => item == _otherSeason ? "season-2" : "season-1", _ => _rateLimitedUntil);

    private static ProviderWriteOutcome Result(OutcomeKind kind, string step, ApiName provider = ApiName.AniList, bool meaningful = false) =>
        new(provider, "500", "Fixture Show", kind, null, null, false, step, meaningful);

    private static RetrySubject Playback(Guid item) => new(item, OperationKind.Playback);

    private RetryDecision[] After(RetrySubject subject, params ProviderWriteOutcome[] outcomes) =>
        _queue.AfterOperation(Guid.NewGuid(), _user, subject, outcomes).ToArray();

    [TestCase("rate-limited")]
    [TestCase("transient")]
    public void AFailureThatMayPassIsRetriedInTwoMinutes(string step) {
        var decisions = After(Playback(_episode5), Result(OutcomeKind.Failed, step));

        Assert.That(decisions.Single().Kind, Is.EqualTo(RetryDecisionKind.Scheduled));
        var row = _queue.ForUser(_user).Single();
        Assert.That(row.Provider, Is.EqualTo(ApiName.AniList));
        Assert.That(row.SourceItem, Is.EqualTo(_episode5));
        Assert.That(row.Trigger, Is.EqualTo(OperationKind.Playback));
        Assert.That(row.Attempt, Is.EqualTo(1));
        Assert.That(row.DueUtc, Is.EqualTo(Start.AddMinutes(2)));
        Assert.That(row.LastStep, Is.EqualTo(step));
    }

    [TestCase(OutcomeKind.Failed, "unauthorized")]
    [TestCase(OutcomeKind.Failed, "rejected")]
    [TestCase(OutcomeKind.Unconfirmed, "transient")]
    [TestCase(OutcomeKind.Unresolved, "resolve")]
    [TestCase(OutcomeKind.Skipped, "plan-to-watch-only")]
    [TestCase(OutcomeKind.NoChange, "already-watched")]
    [TestCase(OutcomeKind.Confirmed, "progress")]
    public void OtherResultsAreNeverRetried(OutcomeKind kind, string step) {
        After(Playback(_episode5), Result(kind, step));

        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void TheBackOffRunsTwoMinutesTenMinutesOneHourSixHoursThenGivesUp() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        var expected = new[] { TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6) };
        for (int attempt = 1; attempt <= 3; attempt++) {
            var row = _queue.ForUser(_user).Single();
            _clock.Now = row.DueUtc;
            Guid retry = Guid.NewGuid();
            _queue.Lease(row.Id, retry);

            _queue.AfterOperation(retry, _user, new RetrySubject(_episode5, OperationKind.Playback, attempt, ApiName.AniList), [Result(OutcomeKind.Failed, "transient")]);

            var next = _queue.ForUser(_user).Single();
            Assert.That(next.Attempt, Is.EqualTo(attempt + 1));
            Assert.That(next.DueUtc, Is.EqualTo(_clock.Now + expected[attempt - 1]));
        }

        var last = _queue.ForUser(_user).Single();
        Guid final = Guid.NewGuid();
        _queue.Lease(last.Id, final);
        var decision = _queue.AfterOperation(final, _user, new RetrySubject(_episode5, OperationKind.Playback, 4, ApiName.AniList),
            [Result(OutcomeKind.Failed, "transient")]).Single();

        Assert.That(decision.Kind, Is.EqualTo(RetryDecisionKind.Dropped));
        Assert.That(decision.Attempt, Is.EqualTo(4));
        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void ASuccessfulRetryClearsTheRow() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        var row = _queue.ForUser(_user).Single();
        Guid retry = Guid.NewGuid();
        _queue.Lease(row.Id, retry);

        var decision = _queue.AfterOperation(retry, _user, new RetrySubject(_episode5, OperationKind.Playback, 1, ApiName.AniList),
            [Result(OutcomeKind.NoChange, "already-watched")]).Single();

        Assert.That(decision.Kind, Is.EqualTo(RetryDecisionKind.Cleared));
        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void ARetryClearsItsOwnRowEvenWhenTheItemNowBelongsToAnotherSeason() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        var row = _queue.ForUser(_user).Single();
        Guid retry = Guid.NewGuid();
        _queue.Lease(row.Id, retry);

        // The library moved the episode, so its season key is different now; the leased row must still go.
        _queue.AfterOperation(retry, _user, new RetrySubject(_otherSeason, OperationKind.Playback, 1, ApiName.AniList),
            [Result(OutcomeKind.NoChange, "already-watched")]);

        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void ARetryWhoseTrackerRecordedNothingClearsTheRow() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        var row = _queue.ForUser(_user).Single();
        Guid retry = Guid.NewGuid();
        _queue.Lease(row.Id, retry);

        // The account was unlinked since, so the pipeline asked no tracker.
        _queue.AfterOperation(retry, _user, new RetrySubject(_episode5, OperationKind.Playback, 1, ApiName.AniList), []);

        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void ANewerCompletionInTheSeasonReplacesThePendingRetryAndStartsAgain() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        _clock.Now += TimeSpan.FromMinutes(1);

        After(Playback(_episode6), Result(OutcomeKind.Failed, "rate-limited"));

        var replaced = _queue.ForUser(_user).Single();
        Assert.That(replaced.SourceItem, Is.EqualTo(_episode6));
        Assert.That(replaced.Attempt, Is.EqualTo(1));
        Assert.That(replaced.DueUtc, Is.EqualTo(_clock.Now.AddMinutes(2)));
        Assert.That(replaced.LastStep, Is.EqualTo("rate-limited"));
    }

    [Test]
    public void ANewerCompletionThatSucceedsClearsThePendingRetry() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));

        After(Playback(_episode6), Result(OutcomeKind.Confirmed, "progress", meaningful: true));

        Assert.That(_queue.ForUser(_user), Is.Empty);
    }

    [Test]
    public void AnOlderRetryFinishingAfterANewerCompletionLeavesTheNewerRow() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        var row = _queue.ForUser(_user).Single();
        Guid retry = Guid.NewGuid();
        _queue.Lease(row.Id, retry);
        // Episode 6 ran first and failed too, so its retry took the row.
        After(Playback(_episode6), Result(OutcomeKind.Failed, "transient"));

        _queue.AfterOperation(retry, _user, new RetrySubject(_episode5, OperationKind.Playback, 1, ApiName.AniList), [Result(OutcomeKind.Confirmed, "progress", meaningful: true)]);

        var kept = _queue.ForUser(_user).Single();
        Assert.That(kept.SourceItem, Is.EqualTo(_episode6));
        Assert.That(kept.Attempt, Is.EqualTo(1));
    }

    [Test]
    public void AnotherSeasonKeepsItsOwnRetry() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));

        After(Playback(_otherSeason), Result(OutcomeKind.Failed, "transient"));

        Assert.That(_queue.ForUser(_user).Select(row => row.SeriesKey), Is.EquivalentTo(new[] { "season-1", "season-2" }));
    }

    [Test]
    public void EachTrackerGetsItsOwnRetryAndOnlyTheFailedOne() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient", ApiName.AniList), Result(OutcomeKind.Confirmed, "progress", ApiName.Kitsu, true),
            Result(OutcomeKind.Failed, "rate-limited", ApiName.Mal));

        Assert.That(_queue.ForUser(_user).Select(row => row.Provider), Is.EquivalentTo(new[] { ApiName.AniList, ApiName.Mal }));

        var kitsuNow = After(Playback(_episode6), Result(OutcomeKind.Confirmed, "progress", ApiName.Kitsu, true));

        Assert.That(kitsuNow, Is.Empty);
        Assert.That(_queue.ForUser(_user), Has.Count.EqualTo(2));
    }

    [Test]
    public void ALaterRateLimitWindowWinsOverTheBackOff() {
        _rateLimitedUntil = Start.AddMinutes(15);

        After(Playback(_episode5), Result(OutcomeKind.Failed, "rate-limited"));

        Assert.That(_queue.ForUser(_user).Single().DueUtc, Is.EqualTo(Start.AddMinutes(15)));
    }

    [Test]
    public void AnEarlierRateLimitWindowLeavesTheBackOff() {
        _rateLimitedUntil = Start.AddSeconds(30);

        After(Playback(_episode5), Result(OutcomeKind.Failed, "rate-limited"));

        Assert.That(_queue.ForUser(_user).Single().DueUtc, Is.EqualTo(Start.AddMinutes(2)));
    }

    [Test]
    public void DueListsOnlyRetriesWhoseTimeHasComeAndALeaseHoldsThemBack() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));
        Assert.That(_queue.Due(), Is.Empty);

        _clock.Now = Start.AddMinutes(2);
        var due = _queue.Due().Single();
        _queue.Lease(due.Id, Guid.NewGuid());

        Assert.That(_queue.Due(), Is.Empty);
        _clock.Now += RetryQueue.LeaseTime;
        Assert.That(_queue.Due().Single().Attempt, Is.EqualTo(1));
    }

    [Test]
    public void RetriesSurviveARestart() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));

        _clock.Now = Start.AddMinutes(3);
        Assert.That(Queue().Due().Single().SourceItem, Is.EqualTo(_episode5));
    }

    [Test]
    public void RemoveDeletesOneRow() {
        After(Playback(_episode5), Result(OutcomeKind.Failed, "transient"));

        _queue.Remove(_queue.ForUser(_user).Single().Id);

        Assert.That(_queue.ForUser(_user), Is.Empty);
    }
}
