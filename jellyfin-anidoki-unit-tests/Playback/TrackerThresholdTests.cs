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

    // User's choice, admin's default, server's percentage → what applies (null: player exit).
    [TestCase(null, null, 85, null)]
    [TestCase(null, 70, 85, 70)]
    [TestCase(0, 70, 85, null)]
    [TestCase(65, 70, 85, 65)]
    [TestCase(65, null, 85, 65)]
    [TestCase(90, 70, 85, 70)]
    [TestCase(90, null, 85, null)]
    [TestCase(null, 90, 85, null)]
    public void AUserFollowsTheAdminsDefaultUnlessTheyChoose(int? user, int? serverDefault, int maxResumePct, int? applies) =>
        Assert.That(TrackerThreshold.ForUser(user, serverDefault, maxResumePct), Is.EqualTo(applies));

    [TestCase(null, true)]
    [TestCase(0, true)]
    [TestCase(70, true)]
    [TestCase(5, false)]
    [TestCase(95, false)]
    public void AUserMayFollowTheDefaultChoosePlayerExitOrAPercentage(int? chosen, bool valid) =>
        Assert.That(TrackerThreshold.IsValidForUser(chosen, 90), Is.EqualTo(valid));
}
