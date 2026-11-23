using System;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers;

[TestFixture]
public class ScoreScaleTests {
    private static readonly ScoreFormat[] AllFormats = Enum.GetValues<ScoreFormat>();

    private static decimal Shown(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [TestCase(ScoreFormat.Point100, 100, "1")]
    [TestCase(ScoreFormat.Point10Decimal, 10, "0.1")]
    [TestCase(ScoreFormat.Point10Half, 10, "0.5")]
    [TestCase(ScoreFormat.Point10, 10, "1")]
    [TestCase(ScoreFormat.Point5, 5, "1")]
    [TestCase(ScoreFormat.Point3, 3, "1")]
    public void EachFormatHasItsMaximumAndStep(ScoreFormat format, int maximum, string step) {
        Assert.That(ScoreScale.Maximum(format), Is.EqualTo(maximum));
        Assert.That(ScoreScale.Step(format), Is.EqualTo(Shown(step)));
    }

    [TestCase(85, ScoreFormat.Point100, "85")]
    [TestCase(85, ScoreFormat.Point10Decimal, "8.5")]
    [TestCase(87, ScoreFormat.Point10Half, "8.5")]
    [TestCase(85, ScoreFormat.Point10, "9")]
    [TestCase(1, ScoreFormat.Point10, "1")]
    [TestCase(85, ScoreFormat.Point5, "4")]
    [TestCase(5, ScoreFormat.Point5, "1")]
    [TestCase(35, ScoreFormat.Point3, "1")]
    [TestCase(36, ScoreFormat.Point3, "2")]
    [TestCase(60, ScoreFormat.Point3, "2")]
    [TestCase(61, ScoreFormat.Point3, "3")]
    [TestCase(100, ScoreFormat.Point3, "3")]
    public void RawConvertsToTheFormat(int raw, ScoreFormat format, string shown) =>
        Assert.That(ScoreScale.ToDisplay(raw, format), Is.EqualTo(Shown(shown)));

    [Test]
    public void UnratedStaysUnrated() {
        foreach (var format in AllFormats) {
            Assert.That(ScoreScale.ToDisplay(null, format), Is.Null, format.ToString());
            Assert.That(ScoreScale.ToRaw(null, format), Is.Null, format.ToString());
        }
    }

    [TestCase("8.5", ScoreFormat.Point10Half, 85)]
    [TestCase("7.3", ScoreFormat.Point10Decimal, 73)]
    [TestCase("4", ScoreFormat.Point5, 80)]
    [TestCase("1", ScoreFormat.Point3, 35)]
    [TestCase("2", ScoreFormat.Point3, 60)]
    [TestCase("3", ScoreFormat.Point3, 85)]
    public void DisplayConvertsToRaw(string shown, ScoreFormat format, int raw) =>
        Assert.That(ScoreScale.ToRaw(Shown(shown), format), Is.EqualTo(raw));

    [TestCase("0", ScoreFormat.Point10)]
    [TestCase("8.3", ScoreFormat.Point10Half)]
    [TestCase("11", ScoreFormat.Point10)]
    [TestCase("6", ScoreFormat.Point5)]
    public void DisplayOutsideTheFormatIsRejected(string shown, ScoreFormat format) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreScale.ToRaw(Shown(shown), format));

    [Test]
    public void EveryValidDisplayRoundTrips() {
        foreach (var format in AllFormats) {
            decimal step = ScoreScale.Step(format);
            int count = (int)(ScoreScale.Maximum(format) / step);
            foreach (decimal shown in Enumerable.Range(1, count).Select(i => i * step).Where(shown => shown >= ScoreScale.Minimum(format)))
                Assert.That(ScoreScale.ToDisplay(ScoreScale.ToRaw(shown, format), format), Is.EqualTo(shown), $"{format} {shown}");
        }
    }

    [TestCase(ScoreFormat.Point10Decimal, ScoreFormat.Point10Half, true)]
    [TestCase(ScoreFormat.Point10, ScoreFormat.Point10Decimal, true)]
    [TestCase(ScoreFormat.Point100, ScoreFormat.Point10, false)]
    [TestCase(ScoreFormat.Point5, ScoreFormat.Point3, false)]
    public void SameScaleMeansTheSameMaximum(ScoreFormat a, ScoreFormat b, bool same) =>
        Assert.That(ScoreScale.SameScale(a, b), Is.EqualTo(same));

    [TestCase(ApiName.Mal, ScoreFormat.Point10)]
    [TestCase(ApiName.Kitsu, ScoreFormat.Point10Half)]
    [TestCase(ApiName.Shikimori, ScoreFormat.Point10)]
    [TestCase(ApiName.Simkl, ScoreFormat.Point10)]
    public void FixedFormats(ApiName provider, ScoreFormat format) =>
        Assert.That(ScoreScale.FixedFormat(provider), Is.EqualTo(format));

    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Annict)]
    public void NoFixedFormat(ApiName provider) =>
        Assert.That(ScoreScale.FixedFormat(provider), Is.Null);


    [Test]
    public void KitsusHalfStepsStartAtOne() {
        Assert.That(ScoreScale.Minimum(ScoreFormat.Point10Half), Is.EqualTo(1m));
        Assert.That(ScoreScale.Minimum(ScoreFormat.Point10Decimal), Is.EqualTo(Shown("0.1")));
        Assert.That(ScoreScale.ToDisplay(2, ScoreFormat.Point10Half), Is.EqualTo(1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreScale.ToRaw(Shown("0.5"), ScoreFormat.Point10Half));
    }
}
