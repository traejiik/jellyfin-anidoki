using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Resolution;

[TestFixture]
public class TitleMatchingTests {
    private static EntryTitles Titles(string? main = null, string? en = null, string? ja = null, params string[] synonyms) => new(main, en, ja, synonyms);

    [TestCase("Otome Kaijuu Caramelise", "otome kaijuu caramelise")]
    [TestCase("Re:Zero", "Re Zero")]
    [TestCase("JoJo's Bizarre Adventure", "JoJos Bizarre Adventure")]
    public void MatchesIgnoringCaseAndSymbols(string tracker, string jellyfin) =>
        Assert.That(TitleMatching.Matches(Titles(main: tracker), [jellyfin]), Is.True);

    [Test]
    public void MatchesOnAnyTitleAgainstAnyCandidate() {
        var titles = Titles("Romaji", "English", "日本語", "Alias");
        Assert.That(TitleMatching.Matches(titles, ["nope", "日本語"]), Is.True);
        Assert.That(TitleMatching.Matches(titles, ["alias"]), Is.True);
    }

    [Test]
    public void IgnoresBlankCandidatesAndNullTitles() =>
        Assert.That(TitleMatching.Matches(Titles(), ["", " ", null!]), Is.False);

    [Test]
    public void DisplayPrefersMainThenFirstSynonymThenJapanese() {
        Assert.That(TitleMatching.Display(Titles("Main", ja: "Ja", synonyms: "Syn")), Is.EqualTo("Main"));
        Assert.That(TitleMatching.Display(Titles(" ", ja: "Ja", synonyms: "Syn")), Is.EqualTo("Syn"));
        Assert.That(TitleMatching.Display(Titles(null, ja: "Ja")), Is.EqualTo("Ja"));
        Assert.That(TitleMatching.Display(null), Is.EqualTo(""));
    }

    [TestCase("Fixture OVA: The Lost Chapter", "fixture ova", true)]
    [TestCase("Fixture OVA", "Another Name", false)]
    public void ContainsExtendedIgnoresSymbolsAndCase(string title, string episodeName, bool expected) =>
        Assert.That(TitleMatching.ContainsExtended(title, episodeName), Is.EqualTo(expected));
}
