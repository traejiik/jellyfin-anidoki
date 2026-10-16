using System;
using System.IO;
using System.Text.Json;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications.Discord;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.NotificationTests.Discord;

[TestFixture]
public class DiscordSettingsTests {
    private Mock<IXmlSerializer> _xml = null!;
    private string _directory = null!; private Guid _user; private DiscordSettingsService _service = null!;
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/fixture_secret";
    [SetUp] public void Setup() { _directory = Path.Combine(Path.GetTempPath(), "discord-settings-" + Guid.NewGuid()); Directory.CreateDirectory(_directory); var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.PluginsPath).Returns(_directory); paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory); var xml = _xml = new Mock<IXmlSerializer>(); xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration { callbackUrl = "https://jf.example/jellyfin" }); _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance); _user = Guid.NewGuid(); _service = new(); }
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);
    [Test] public void KeepRetainsSecretAndClearDisablesWithoutLeakingIt() { Assert.That(_service.Read(_user).Enabled, Is.False); var replace = _service.Save(_user, new(true, DiscordWebhookAction.Replace, Webhook, 0)); Assert.That(replace.Status, Is.EqualTo(200)); var keep = _service.Save(_user, new(false, DiscordWebhookAction.Keep, null, replace.Settings!.Revision)); Assert.That(keep.Settings!.HasWebhook, Is.True); Assert.That(JsonSerializer.Serialize(keep.Settings), Does.Not.Contain("fixture_secret")); var config = Plugin.Instance!.Configuration.UserConfig[0]; Assert.That(JsonSerializer.Serialize(new UserConfigurationResponse(config)), Does.Not.Contain("fixture_secret")); var clear = _service.Save(_user, new(true, DiscordWebhookAction.Clear, null, keep.Settings.Revision)); Assert.That(clear.Settings!.HasWebhook, Is.False); Assert.That(clear.Settings.Enabled, Is.False); }
    [Test] public void StaleSettingsCannotOverwriteAnotherSave() { _service.Save(_user, new(false, DiscordWebhookAction.Replace, Webhook, 0)); Assert.That(_service.Save(_user, new(true, DiscordWebhookAction.Clear, null, 0)).Status, Is.EqualTo(409)); Assert.That(_service.Read(_user).HasWebhook, Is.True); }
    [Test] public void RejectedRevisionIsBlockedUntilExplicitRetest() { var saved = _service.Save(_user, new(true, DiscordWebhookAction.Replace, Webhook, 0)).Settings!; _service.Block(_user, saved.Revision); Assert.That(_service.Destination(_user, saved.Revision), Is.Null); Assert.That(_service.Destination(_user, saved.Revision, test: true), Is.Not.Null); _service.Unblock(_user, saved.Revision); Assert.That(_service.Destination(_user, saved.Revision), Is.Not.Null); }
    [Test]
    public void ResponseUsesTheBrowserContractCasing() {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(_service.Read(_user)));
        Assert.That(json.RootElement.TryGetProperty("enabled", out _), Is.True);
        Assert.That(json.RootElement.TryGetProperty("hasWebhook", out _), Is.True);
        Assert.That(json.RootElement.TryGetProperty("publicAddressReady", out _), Is.True);
    }

    [Test]
    public void FailedPersistenceDoesNotEnableAnUnsavedDestination() {
        _xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>())).Throws(new IOException("fixture"));
        Assert.That(_service.Save(_user, new(true, DiscordWebhookAction.Replace, Webhook, 0)).Status, Is.EqualTo(503));
        Assert.That(_service.Read(_user).HasWebhook, Is.False);
        Assert.That(_service.Read(_user).Revision, Is.Zero);
    }

}
