using System;
using System.IO;
using System.Text.Json;
using System.Xml.Serialization;
using jellyfin_anidoki.Models;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture]
public class UserConfigDefaultsTests
{
    [TestCase(typeof(UserConfig), false, null, true)]
    [TestCase(typeof(UserConfig), false, true, true)]
    [TestCase(typeof(UserConfig), false, false, false)]
    [TestCase(typeof(UserConfig), true, null, true)]
    [TestCase(typeof(UserConfig), true, true, true)]
    [TestCase(typeof(UserConfig), true, false, false)]
    [TestCase(typeof(UserEditableConfig), false, null, true)]
    [TestCase(typeof(UserEditableConfig), false, true, true)]
    [TestCase(typeof(UserEditableConfig), false, false, false)]
    [TestCase(typeof(UserEditableConfig), true, null, true)]
    [TestCase(typeof(UserEditableConfig), true, true, true)]
    [TestCase(typeof(UserEditableConfig), true, false, false)]
    public void NotificationPreferenceMigrationRetainsDefaultAndExplicitValues(Type model, bool xml, bool? savedValue, bool expected)
    {
        object result;
        if (xml) {
            var field = savedValue.HasValue ? $"<ShowLogNotifications>{savedValue.Value.ToString().ToLowerInvariant()}</ShowLogNotifications>" : string.Empty;
            using var reader = new StringReader($"<{model.Name}>{field}</{model.Name}>");
            result = new XmlSerializer(model).Deserialize(reader)!;
        } else {
            var json = savedValue.HasValue ? $"{{\"ShowLogNotifications\":{savedValue.Value.ToString().ToLowerInvariant()}}}" : "{}";
            result = JsonSerializer.Deserialize(json, model)!;
        }
        var serialized = JsonSerializer.SerializeToElement(result, model);
        Assert.That(serialized.TryGetProperty("ShowLogNotifications", out var preference), Is.True,
            "Notification preference must be present after old/new configuration deserialization");
        Assert.That(preference.GetBoolean(), Is.EqualTo(expected));
    }

    [Test]
    public void NewStoredUserRetainsExistingPreferenceDefaults()
    {
        var config = new UserConfig();
        Assert.Multiple(() => {
            Assert.That(config.PlanToWatchOnly, Is.True);
            Assert.That(config.RewatchCompleted, Is.True);
            Assert.That(config.ShowLogNotifications, Is.True);
        });
    }
}
