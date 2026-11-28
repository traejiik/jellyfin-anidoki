using System;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Trackers.AniList;
using jellyfin_anidoki_unit_tests.Trackers.Kitsu;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

/// <summary>D7: an account needs attention when its newest decisive result is a rejected sign-in or a retry that gave up.</summary>
[TestFixture]
public class AccountAttentionTests {
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private MutableTime _clock = null!;
    private OperationStore _store = null!;
    private AccountAttention _attention = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _clock = new MutableTime(Start);
        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _store = new OperationStore(database, _clock);
        _attention = new AccountAttention(database);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    /// <summary>Records one finished operation with one result, ten minutes after the last.</summary>
    private void Ran(OutcomeKind kind, string step, ApiName provider = ApiName.AniList, OperationKind operation = OperationKind.Playback,
        int? attempt = null, Guid? user = null) {
        _clock.Now += TimeSpan.FromMinutes(10);
        Guid id = Guid.NewGuid();
        _store.Create(id, user ?? _user, operation, "Fixture Show · episode 5", retryAttempt: attempt);
        _store.AddAction(id, 1, new ProviderWriteOutcome(provider, "500", "Fixture Show", kind, null, null, false, step, kind == OutcomeKind.Confirmed));
        _store.Finish(id, kind == OutcomeKind.Confirmed ? OperationState.Succeeded : OperationState.Failed, null);
    }

    [Test]
    public void NothingRecordedNeedsNoAttention() => Assert.That(_attention.For(_user), Is.Empty);

    [Test]
    public void ARejectedSignInNeedsAttentionSinceTheFirstRejection() {
        Ran(OutcomeKind.Confirmed, "progress");
        Ran(OutcomeKind.Failed, "unauthorized");
        DateTimeOffset first = _clock.Now;
        Ran(OutcomeKind.Failed, "unauthorized");

        var attention = _attention.For(_user).Single();

        Assert.That(attention.Provider, Is.EqualTo(ApiName.AniList));
        Assert.That(attention.Kind, Is.EqualTo(AttentionKind.SignInRejected));
        Assert.That(attention.Since, Is.EqualTo(first));
    }

    [Test]
    public void ALaterSuccessClearsIt() {
        Ran(OutcomeKind.Failed, "unauthorized");
        Ran(OutcomeKind.NoChange, "already-watched");

        Assert.That(_attention.For(_user), Is.Empty);
    }

    [Test]
    public void ASkipOrNoMatchSaysNothingAboutTheAccount() {
        Ran(OutcomeKind.Failed, "unauthorized");
        Ran(OutcomeKind.Skipped, "plan-to-watch-only");
        Ran(OutcomeKind.Unresolved, "resolve");

        Assert.That(_attention.For(_user).Single().Kind, Is.EqualTo(AttentionKind.SignInRejected));
    }

    [TestCase("rate-limited")]
    [TestCase("transient")]
    public void ARetryThatGaveUpNeedsAttention(string step) {
        Ran(OutcomeKind.Failed, step);
        for (int attempt = 1; attempt <= RetryQueue.MaxAttempts; attempt++)
            Ran(OutcomeKind.Failed, step, operation: OperationKind.Retry, attempt: attempt);

        var attention = _attention.For(_user).Single();

        Assert.That(attention.Kind, Is.EqualTo(AttentionKind.UpdatesFailing));
        Assert.That(attention.Step, Is.EqualTo(step));
        Assert.That(attention.Since, Is.EqualTo(_clock.Now));
    }

    [Test]
    public void ARetryStillPendingNeedsNone() {
        Ran(OutcomeKind.Failed, "transient");
        Ran(OutcomeKind.Failed, "transient", operation: OperationKind.Retry, attempt: 2);

        Assert.That(_attention.For(_user), Is.Empty);
    }

    [Test]
    public void EachTrackerAndUserIsJudgedOnItsOwn() {
        Guid other = Guid.NewGuid();
        Ran(OutcomeKind.Failed, "unauthorized", ApiName.Kitsu);
        Ran(OutcomeKind.Confirmed, "progress", ApiName.AniList);
        Ran(OutcomeKind.Failed, "unauthorized", ApiName.AniList, user: other);

        Assert.That(_attention.For(_user).Select(item => item.Provider), Is.EqualTo(new[] { ApiName.Kitsu }));
        var everyone = _attention.ForAll();
        Assert.That(everyone.Select(item => (item.UserId, item.Provider)),
            Is.EquivalentTo(new[] { (_user, ApiName.Kitsu), (other, ApiName.AniList) }));
    }
}
