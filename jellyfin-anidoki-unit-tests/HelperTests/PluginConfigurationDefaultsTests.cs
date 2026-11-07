using System.IO;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture]
public class PluginConfigurationDefaultsTests {
    // Jellyfin reads the plugin XML with XmlSerializer, which keeps the constructor's value for a missing element.
    [TestCase("<PluginConfiguration />", true)]
    [TestCase("<PluginConfiguration><applySavedMatches>true</applySavedMatches></PluginConfiguration>", true)]
    [TestCase("<PluginConfiguration><applySavedMatches>false</applySavedMatches></PluginConfiguration>", false)]
    public void SavedMatchesAreOnUnlessSwitchedOff(string xml, bool expected) {
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
        Assert.That(config.applySavedMatches, Is.EqualTo(expected));
    }

    [Test]
    public void ANewConfigurationUsesSavedMatches() => Assert.That(new PluginConfiguration().applySavedMatches, Is.True);
}
