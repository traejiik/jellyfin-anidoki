using jellyfin_anidoki.Helpers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.LoggingTests;

[TestFixture]
public class LogTextTests {
    [Test]
    public void AShortBodyIsKeptWhole() => Assert.That(LogText.Truncate("{\"error\":\"not_found\"}"), Is.EqualTo("{\"error\":\"not_found\"}"));

    [Test]
    public void ALongBodyIsCutTo400CharactersAndMarked() {
        string cut = LogText.Truncate(new string('x', 401));

        Assert.That(cut, Is.EqualTo(new string('x', 400) + "..."));
    }

    [Test]
    public void NoBodyIsEmpty() => Assert.That(LogText.Truncate(null), Is.EqualTo(""));
}
