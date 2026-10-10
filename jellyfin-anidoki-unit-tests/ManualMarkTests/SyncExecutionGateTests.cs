using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class SyncExecutionGateTests
{
    [Test]
    public async Task SameUserWaitsWhileOtherUserCanProceed()
    {
        var gate = new SyncExecutionGate(); var user = Guid.NewGuid();
        using var first = await gate.Enter(user);
        var waiting = gate.Enter(user);
        using var other = await gate.Enter(Guid.NewGuid());
        Assert.That(waiting.IsCompleted, Is.False);
        first.Dispose(); using var second = await waiting;
    }
    [Test]
    public async Task CancelledWaitDoesNotLeakOrReleaseTheActiveLease()
    {
        var gate = new SyncExecutionGate(); var user = Guid.NewGuid();
        using var first = await gate.Enter(user); using var cancellation = new CancellationTokenSource();
        var wait = gate.Enter(user, cancellation.Token); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await wait);
        var next = gate.Enter(user); Assert.That(next.IsCompleted, Is.False);
        first.Dispose(); using var final = await next;
    }
}
