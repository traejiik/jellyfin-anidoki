using System.Linq;
using jellyfin_anidoki.Playback;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>A progress report every second used to write a line every second; now a session writes one a minute and on changes.</summary>
[TestFixture]
public class ProgressLogGateTests {
    [Test]
    public void PlayingWritesTheFirstReportAndThenOneAMinute() {
        var gate = new ProgressLogGate();

        var logged = Enumerable.Range(0, 181).Where(second => gate.ShouldLog("session", second, false)).ToList();

        Assert.That(logged, Is.EqualTo(new[] { 0, 60, 120, 180 }));
    }

    [Test]
    public void APauseAndAResumeEachWriteALine() {
        var gate = new ProgressLogGate();
        gate.ShouldLog("session", 10, false);

        Assert.That(gate.ShouldLog("session", 11, true), Is.True);
        Assert.That(gate.ShouldLog("session", 11, true), Is.False);
        Assert.That(gate.ShouldLog("session", 11, false), Is.True);
    }

    [Test]
    public void ASeekWritesALine() {
        var gate = new ProgressLogGate();
        gate.ShouldLog("session", 10, false);

        Assert.That(gate.ShouldLog("session", 600, false), Is.True);
        Assert.That(gate.ShouldLog("session", 601, false), Is.False);
        Assert.That(gate.ShouldLog("session", 300, false), Is.True);
    }

    [Test]
    public void EachSessionIsCountedOnItsOwnAndAStopForgetsIt() {
        var gate = new ProgressLogGate();
        gate.ShouldLog("first", 10, false);

        Assert.That(gate.ShouldLog("second", 11, false), Is.True);
        Assert.That(gate.ShouldLog("first", 11, false), Is.False);
        gate.Forget("first");
        Assert.That(gate.ShouldLog("first", 12, false), Is.True);
    }
}
