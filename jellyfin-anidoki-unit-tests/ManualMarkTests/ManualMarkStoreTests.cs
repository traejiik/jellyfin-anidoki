using System;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.ManualMarks;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkStoreTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private Clock _clock = null!;
    private ManualMarkStore _store = null!;
    private ManualMarkRequest _request = null!;
    [SetUp]
    public void Setup()
    {
        _clock = new(); _store = new(_clock);
        _request = new(Guid.NewGuid(), Guid.NewGuid(), "fixture-identity", Guid.NewGuid(),
            "Fixture series", 12, "Fixture episode", _clock.Now, "fixture-account");
    }
    [Test]
    public void TerminalCompletionKeepsImmutableRecoveryStateAndCannotBeClaimedAgain()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        _store.TryBegin(pending.OperationId, out _);
        _store.Complete(pending.OperationId, ManualMarkState.NoChange, [], "Already watched on tracker.");
        var completed = _store.Read(_request.UserId, _request.AuthenticationKey, pending.OperationId)!;
        Assert.That(completed.State, Is.EqualTo(ManualMarkState.NoChange));
        Assert.That(completed.Revision, Is.GreaterThan(pending.Revision));
        Assert.That(_store.TryBegin(pending.OperationId, out _), Is.False);
        Assert.That(_store.Read(_request.UserId, "foreign", pending.OperationId), Is.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(completed);
        Assert.That(json, Does.Contain("\"state\":\"NoChange\""));
        Assert.That(json, Does.Not.Contain("fixture-identity").And.Not.Contain("fixture-account"));
        _clock.Now += TimeSpan.FromMinutes(10);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey), Is.Empty);
    }
    [Test]
    public void GlobalCapacityDoesNotLoseExistingPendingOperations()
    {
        for (var i = 0; i < 1000; i++)
            Assert.That(_store.CreateOrGet(_request with { OperationId = Guid.NewGuid(), UserId = Guid.NewGuid() }), Is.Not.Null);
        Assert.That(_store.CreateOrGet(_request), Is.Null);
    }
    [Test]
    public void DisableCancelsPendingAndQueuedButDoesNotClaimToUndoSending()
    {
        var pending = _store.CreateOrGet(_request)!;
        var other = _store.CreateOrGet(_request with { OperationId = Guid.NewGuid(), SourceItemId = Guid.NewGuid() })!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, other.OperationId, other.Revision);
        _store.TryBegin(other.OperationId, out _);
        _store.CancelPending("Manual updates disabled.");
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey, pending.OperationId)!.State, Is.EqualTo(ManualMarkState.Cancelled));
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey, other.OperationId)!.State, Is.EqualTo(ManualMarkState.Sending));
    }
    [Test]
    public void RejectedQueueReservationRestoresPendingAndPlaybackSupersedesQueuedWork()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        _store.RestorePending(pending.OperationId);
        var restored = _store.Read(_request.UserId, _request.AuthenticationKey).Single();
        Assert.That(restored.State, Is.EqualTo(ManualMarkState.Pending));
        Assert.That(restored.Revision, Is.GreaterThan(pending.Revision));
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, restored.OperationId, restored.Revision);
        _store.Supersede(_request.UserId, _request.SourceItemId);
        Assert.That(_store.TryBegin(restored.OperationId, out _), Is.False);
    }
    [Test]
    public void UnsupportedMarkNoticeIsTerminalAndNeverQueuesAWrite()
    {
        var notice = _store.RecordSuppressed(_request, "Use manual sync.");
        Assert.That(notice!.State, Is.EqualTo(ManualMarkState.Skipped));
        Assert.That(_store.TryQueue(_request.UserId, _request.AuthenticationKey, notice.OperationId, notice.Revision), Is.False);
    }
    [Test]
    public void RepeatedConfirmQueuesOnlyOnce()
    {
        var pending = _store.CreateOrGet(_request)!;
        Assert.That(_store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision), Is.True);
        Assert.That(_store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision), Is.False);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey).Single().State, Is.EqualTo(ManualMarkState.Queued));
    }
    [Test]
    public void ForeignIdentityAndStaleRevisionCannotConfirm()
    {
        var pending = _store.CreateOrGet(_request)!;
        Assert.That(_store.TryQueue(Guid.NewGuid(), _request.AuthenticationKey, pending.OperationId, pending.Revision), Is.False);
        Assert.That(_store.TryQueue(_request.UserId, "foreign", pending.OperationId, pending.Revision), Is.False);
        Assert.That(_store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision + 1), Is.False);
        Assert.That(_store.Read(Guid.NewGuid(), _request.AuthenticationKey), Is.Empty);
    }
    [Test]
    public void DuplicateTransitionUsesExistingOperationButUnmarkAndRemarkGetsNewOperation()
    {
        var first = _store.CreateOrGet(_request)!;
        Assert.That(_store.CreateOrGet(_request with { OperationId = Guid.NewGuid() })!.OperationId, Is.EqualTo(first.OperationId));
        _store.CancelForSource(_request.UserId, _request.SourceItemId);
        Assert.That(_store.CreateOrGet(_request with { OperationId = Guid.NewGuid() })!.OperationId, Is.Not.EqualTo(first.OperationId));
    }
    [Test]
    public void QueuedUnmarkPreventsClaim()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        _store.CancelForSource(_request.UserId, _request.SourceItemId);
        Assert.That(_store.TryBegin(pending.OperationId, out _), Is.False);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey).Single().State, Is.EqualTo(ManualMarkState.Cancelled));
    }
    [Test]
    public void SendingUnmarkRetainsActualStateAndExplainsNoRollback()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        Assert.That(_store.TryBegin(pending.OperationId, out _), Is.True);
        _store.CancelForSource(_request.UserId, _request.SourceItemId);
        var sending = _store.Read(_request.UserId, _request.AuthenticationKey).Single();
        Assert.That(sending.State, Is.EqualTo(ManualMarkState.Sending));
        Assert.That(sending.Reason, Does.Contain("cannot undo"));
    }
    [Test]
    public void ExpiryStopsQueuedWorkAndTerminalRecoveryIsBounded()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.That(_store.TryBegin(pending.OperationId, out _), Is.False);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey).Single().State, Is.EqualTo(ManualMarkState.Expired));
        _clock.Now += TimeSpan.FromMinutes(10);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey), Is.Empty);
    }
    [Test]
    public void CapacityDoesNotEvictPendingCommands()
    {
        for (var i = 0; i < 10; i++) Assert.That(_store.CreateOrGet(_request with { OperationId = Guid.NewGuid(), SourceItemId = Guid.NewGuid() }), Is.Not.Null);
        Assert.That(_store.CreateOrGet(_request with { OperationId = Guid.NewGuid(), SourceItemId = Guid.NewGuid() }), Is.Null);
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey), Has.Length.EqualTo(10));
    }
    [Test]
    public void ConcurrentClaimsExecuteOnlyOnce()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        var claims = Enumerable.Range(0, 20).AsParallel().Select(_ => _store.TryBegin(pending.OperationId, out var request)).ToArray();
        Assert.That(claims.Count(x => x), Is.EqualTo(1));
    }
    [Test]
    public void DismissQueuedOperationAndDisableSettingNeverCreateSendingWork()
    {
        var pending = _store.CreateOrGet(_request)!;
        _store.TryQueue(_request.UserId, _request.AuthenticationKey, pending.OperationId, pending.Revision);
        var queued = _store.Read(_request.UserId, _request.AuthenticationKey).Single();
        Assert.That(_store.Dismiss(_request.UserId, _request.AuthenticationKey, pending.OperationId, queued.Revision), Is.True);
        Assert.That(_store.TryBegin(pending.OperationId, out _), Is.False);
    }
}
