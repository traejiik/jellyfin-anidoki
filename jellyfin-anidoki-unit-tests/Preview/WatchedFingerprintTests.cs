using jellyfin_anidoki.Preview;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Preview;

[TestFixture]
public class WatchedFingerprintTests {
    [TestCase(new int[0], "")]
    [TestCase(new[] { 5 }, "5")]
    [TestCase(new[] { 1, 2, 3, 4 }, "1-4")]
    [TestCase(new[] { 14, 3, 1, 2, 12, 13, 3 }, "1-3,12-14")]
    [TestCase(new[] { 1, 3, 5 }, "1,3,5")]
    public void PlayedEpisodesAreSortedWithRunsCollapsed(int[] episodes, string expected) =>
        Assert.That(WatchedFingerprint.Of(episodes), Is.EqualTo(expected));
}
