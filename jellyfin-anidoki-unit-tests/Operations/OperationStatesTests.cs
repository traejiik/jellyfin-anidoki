using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

[TestFixture]
public class OperationStatesTests {
    [TestCase(new OutcomeKind[0], OperationState.NoChange)]
    [TestCase(new[] { OutcomeKind.Confirmed }, OperationState.Succeeded)]
    [TestCase(new[] { OutcomeKind.Confirmed, OutcomeKind.Failed }, OperationState.Partial)]
    [TestCase(new[] { OutcomeKind.Confirmed, OutcomeKind.Unresolved }, OperationState.Partial)]
    [TestCase(new[] { OutcomeKind.NoChange, OutcomeKind.Unconfirmed }, OperationState.Unconfirmed)]
    [TestCase(new[] { OutcomeKind.NoChange, OutcomeKind.Failed }, OperationState.Failed)]
    [TestCase(new[] { OutcomeKind.Skipped, OutcomeKind.NoChange }, OperationState.NoChange)]
    [TestCase(new[] { OutcomeKind.Skipped }, OperationState.Skipped)]
    [TestCase(new[] { OutcomeKind.Unresolved }, OperationState.Unresolved)]
    public void TheResultFollowsTheManualMarkRules(OutcomeKind[] kinds, OperationState expected) {
        // Confirmed outcomes count as changes only when meaningful, as the toast and manual marks treat them.
        var outcomes = kinds.Select(kind => Outcome(kind, meaningful: kind == OutcomeKind.Confirmed)).ToArray();

        Assert.That(OperationStates.FromOutcomes(outcomes), Is.EqualTo(expected));
    }

    [Test]
    public void AConfirmedWriteThatChangedNothingIsNotAChange() =>
        Assert.That(OperationStates.FromOutcomes([Outcome(OutcomeKind.Confirmed, meaningful: false)]), Is.EqualTo(OperationState.Unresolved));

    [TestCase(OperationState.Queued, false)]
    [TestCase(OperationState.Running, false)]
    [TestCase(OperationState.Succeeded, true)]
    [TestCase(OperationState.Cancelled, true)]
    [TestCase(OperationState.Interrupted, true)]
    public void OnlyQueuedAndRunningAreUnfinished(OperationState state, bool finished) =>
        Assert.That(OperationStates.IsFinished(state), Is.EqualTo(finished));
}
