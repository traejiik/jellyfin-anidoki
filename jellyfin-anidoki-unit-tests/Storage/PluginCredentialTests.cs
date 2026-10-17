using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture, NonParallelizable]
public class PluginCredentialTests {
    private string _directory = null!;
    private Mock<IApplicationPaths> _paths = null!;
    private Mock<IXmlSerializer> _xml = null!;
    private readonly Guid _user = Guid.NewGuid();

    private string ConfigurationPath => Path.Combine(_directory, "jellyfin-anidoki.xml");
    private string Owner => _user.ToString("N");

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-plugin-credentials-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _paths = new Mock<IApplicationPaths>();
        _paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        _paths.SetupGet(p => p.DataPath).Returns(_directory);
        _xml = new Mock<IXmlSerializer>();
        // What the real XmlSerializer returns from an old file: every record, but no credential (they are XmlIgnore'd).
        _xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(() => new PluginConfiguration {
            ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Mal, ClientId = "client" }],
            UserConfig = [new UserConfig { UserId = _user, UserApiAuth = [new UserApiAuth { Name = ApiName.AniList }] }]
        });
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private Plugin NewPlugin() => new(_paths.Object, new Mock<IServerConfigurationManager>().Object, _xml.Object, NullLogger<Plugin>.Instance);

    private SecretStore Store() => new(AniDokiDatabase.ForDataPath(_directory));

    private SecretKey Access => new(SecretKey.AccessTokenKind, Owner, "AniList");

    private void WriteLegacyXml() => File.WriteAllText(ConfigurationPath, $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <UserConfig>
            <UserConfig>
              <UserId>{_user}</UserId>
              <DiscordWebhookUrl>https://discord.com/api/webhooks/1/legacy</DiscordWebhookUrl>
              <UserApiAuth>
                <UserApiAuth>
                  <Name>AniList</Name>
                  <AccessToken>legacy-access</AccessToken>
                  <RefreshToken>legacy-refresh</RefreshToken>
                </UserApiAuth>
              </UserApiAuth>
            </UserConfig>
          </UserConfig>
          <ProviderApiAuth>
            <ProviderApiAuth>
              <Name>Mal</Name>
              <ClientId>client</ClientId>
              <ClientSecret>legacy-client-secret</ClientSecret>
            </ProviderApiAuth>
          </ProviderApiAuth>
        </PluginConfiguration>
        """);

    [Test]
    public void ConfigurationFileIsWhereTheTestsWriteIt() =>
        Assert.That(NewPlugin().ConfigurationFilePath, Is.EqualTo(ConfigurationPath));

    [Test]
    public void LegacyCredentialsMoveIntoTheDatabaseOnStartup() {
        WriteLegacyXml();

        var plugin = NewPlugin();

        Assert.That(plugin.StorageError, Is.Null);
        var auth = plugin.Configuration.UserConfig[0].UserApiAuth[0];
        Assert.That(auth.AccessToken, Is.EqualTo("legacy-access"));
        Assert.That(auth.RefreshToken, Is.EqualTo("legacy-refresh"));
        Assert.That(plugin.Configuration.ProviderApiAuth[0].ClientSecret, Is.EqualTo("legacy-client-secret"));
        Assert.That(plugin.Configuration.UserConfig[0].DiscordWebhookUrl, Is.EqualTo("https://discord.com/api/webhooks/1/legacy"));
        Assert.That(Store().ReadAll(), Has.Count.EqualTo(4));
        Assert.That(Store().ReadAll()[Access], Is.EqualTo("legacy-access"));
        _xml.Verify(x => x.SerializeToFile(It.IsAny<object>(), ConfigurationPath), Times.AtLeastOnce);
    }

    [Test]
    public void StoredCredentialsLoadWhenTheXmlHasNone() {
        Store().ReplaceAll(new Dictionary<SecretKey, string> { [Access] = "stored-access" });

        var plugin = NewPlugin();

        Assert.That(plugin.Configuration.UserConfig[0].UserApiAuth[0].AccessToken, Is.EqualTo("stored-access"));
        Assert.That(Store().ReadAll()[Access], Is.EqualTo("stored-access"));
    }

    [Test]
    public void AdminSaveKeepsCredentialsItCannotSee() {
        Store().ReplaceAll(new Dictionary<SecretKey, string> { [Access] = "stored-access" });
        var plugin = NewPlugin();

        plugin.UpdateConfiguration(new PluginConfiguration {
            ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Mal, ClientId = "edited" }],
            UserConfig = [new UserConfig { UserId = _user, UserApiAuth = [new UserApiAuth { Name = ApiName.AniList }] }]
        });

        Assert.That(plugin.Configuration.ProviderApiAuth[0].ClientId, Is.EqualTo("edited"));
        Assert.That(plugin.Configuration.UserConfig[0].UserApiAuth[0].AccessToken, Is.EqualTo("stored-access"));
        Assert.That(Store().ReadAll()[Access], Is.EqualTo("stored-access"));
    }

    [Test]
    public void AdminSaveWithoutAnAccountRemovesItsCredentials() {
        Store().ReplaceAll(new Dictionary<SecretKey, string> { [Access] = "stored-access" });
        var plugin = NewPlugin();

        plugin.UpdateConfiguration(new PluginConfiguration {
            ProviderApiAuth = [],
            UserConfig = [new UserConfig { UserId = _user, UserApiAuth = [] }]
        });

        Assert.That(Store().ReadAll(), Does.Not.ContainKey(Access));
    }

    [Test]
    public void DisconnectingAnAccountRemovesItsCredentials() {
        Store().ReplaceAll(new Dictionary<SecretKey, string> { [Access] = "stored-access" });
        var plugin = NewPlugin();

        plugin.Configuration.UserConfig[0].UserApiAuth = [];
        plugin.SaveConfiguration();

        Assert.That(Store().ReadAll(), Does.Not.ContainKey(Access));
    }

    [Test]
    public void UnavailableStorageRefusesToSave() {
        string file = Path.Combine(_directory, "data-is-a-file");
        File.WriteAllText(file, "fixture");
        _paths.SetupGet(p => p.DataPath).Returns(file);

        var plugin = NewPlugin();

        Assert.That(plugin.StorageError, Is.Not.Null);
        Assert.Throws<InvalidOperationException>(() => plugin.SaveConfiguration());
    }

    [Test]
    public void UnreadableLegacyXmlRefusesToSave() {
        File.WriteAllText(ConfigurationPath, "<PluginConfiguration><UserConfig><UserConfig><UserId>not-a-guid</UserId></UserConfig></UserConfig></PluginConfiguration>");

        var plugin = NewPlugin();

        Assert.That(plugin.StorageError, Does.Contain("could not be read"));
        Assert.Throws<InvalidOperationException>(() => plugin.SaveConfiguration());
        _xml.Verify(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void UninstallRemovesTheDataFolder() {
        var plugin = NewPlugin();
        Assert.That(Directory.Exists(Path.Combine(_directory, "AniDoki")), Is.True);

        plugin.OnUninstalling();

        Assert.That(Directory.Exists(Path.Combine(_directory, "AniDoki")), Is.False);
    }
}
