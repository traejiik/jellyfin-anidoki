using jellyfin_anidoki.Playback;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>D3: a completion percentage from 60% to the server's own, in steps of 5, or none.</summary>
[TestFixture]
public class TrackerThresholdTests {
    [TestCase(null, 90, null)]
    [TestCase(60, 90, 60)]
    [TestCase(75, 90, 75)]
    [TestCase(90, 90, 90)]
    [TestCase(55, 90, null)]
    [TestCase(62, 90, null)]
    [TestCase(95, 90, null)]
    [TestCase(85, 80, null)]
    [TestCase(80, 80, 80)]
    public void OnlyAChoiceTheServerAllowsApplies(int? chosen, int maxResumePct, int? effective) {
        Assert.That(TrackerThreshold.Effective(chosen, maxResumePct), Is.EqualTo(effective));
        Assert.That(TrackerThreshold.IsValid(chosen, maxResumePct), Is.EqualTo(chosen == null || effective != null));
    }
}
