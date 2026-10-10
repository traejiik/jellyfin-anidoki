using System;
using System.IO;
using System.Linq;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests;

[TestFixture, NonParallelizable]
public class PluginIntegrationTests
{
    private string _directory = null!;
    private string _pagesPath = null!;
    private Mock<IApplicationPaths> _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-plugin-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _pagesPath = Path.Combine(_directory, "Jellyfin.Plugin.PluginPages", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_pagesPath)!);
        _paths = new Mock<IApplicationPaths>();
        _paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private Plugin CreatePlugin(bool enableUserPages = false)
    {
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration { enableUserPages = enableUserPages });
        return new Plugin(_paths.Object, CreateServerConfiguration(),
            xml.Object, NullLogger<Plugin>.Instance);
    }

    [Test]
    public void UninstallRemovesOnlyOwnedPageRegistration()
    {
        var plugin = CreatePlugin();
        File.WriteAllText(_pagesPath, """
        {"pages":[{"Id":"jellyfin_anidoki"},{"Id":"other.plugin"}],"keep":true}
        """);
        plugin.OnUninstalling();
        var result = JObject.Parse(File.ReadAllText(_pagesPath));
        Assert.That((bool)result["keep"]!, Is.True);
        Assert.That(((JArray)result["pages"]!).Count, Is.EqualTo(1));
        Assert.That((string)result["pages"]![0]!["Id"]!, Is.EqualTo("other.plugin"));
        Assert.DoesNotThrow(() => plugin.OnUninstalling());
    }

    [Test]
    public void MalformedThirdPartyConfigDoesNotPreventStartupOrUninstall()
    {
        File.WriteAllText(_pagesPath, "not json");
        Plugin? plugin = null;
        Assert.DoesNotThrow(() => plugin = CreatePlugin());
        Assert.DoesNotThrow(() => plugin!.OnUninstalling());
        Assert.That(File.ReadAllText(_pagesPath), Is.EqualTo("not json"));
    }


    [TestCase("not json")]
    [TestCase("{\"pages\":{},\"keep\":true}")]
    [TestCase("{\"pages\":null,\"keep\":true}")]
    [TestCase("{\"pages\":[42],\"keep\":true}")]
    [TestCase("{\"pages\":[{\"Id\":{\"nested\":true}}],\"keep\":true}")]
    [TestCase("{\"pages\":[{\"Id\":\"jellyfin_anidoki\",\"Version\":\"invalid\"}],\"keep\":true}")]
    public void EnabledUserPagesContainMalformedRegistrationWithoutRewriting(string original)
    {
        var seed = CreatePlugin();
        File.WriteAllText(seed.ConfigurationFilePath, "fixture");
        File.WriteAllText(_pagesPath, original);
        Plugin? plugin = null;
        Assert.DoesNotThrow(() => plugin = CreatePlugin(true));
        Assert.That(plugin!.PluginConfiguration.enableUserPages, Is.True);
        Assert.That(File.ReadAllText(_pagesPath), Is.EqualTo(original));
    }

    [Test]
    public void DisablingUserPagesRemovesOwnedRegistrationDuringStartup()
    {
        File.WriteAllText(_pagesPath, "{\"pages\":[{\"Id\":\"jellyfin_anidoki\"},{\"Id\":\"other.plugin\"}]}");
        CreatePlugin();
        Assert.That((string)JObject.Parse(File.ReadAllText(_pagesPath))["pages"]![0]!["Id"]!,
            Is.EqualTo("other.plugin"));
    }

    [Test]
    public void EnablingUserPagesUpgradesOwnedMenuMetadataAndPreservesOtherPages()
    {
        var seed = CreatePlugin();
        File.WriteAllText(seed.ConfigurationFilePath, "fixture");
        File.WriteAllText(_pagesPath, """
        {"pages":[{"Id":"jellyfin_anidoki","DisplayText":"AniDoki Configuration","Icon":"build","Version":1},{"Id":"other.plugin","Icon":"star"}],"keep":true}
        """);

        var plugin = CreatePlugin(true);
        var result = JObject.Parse(File.ReadAllText(_pagesPath));
        var pages = (JArray)result["pages"]!;
        var owned = pages.Single(page => (string?)page["Id"] == "jellyfin_anidoki");
        Assert.Multiple(() => {
            Assert.That((string?)owned["DisplayText"], Is.EqualTo("AniDōki"));
            Assert.That((string?)owned["Icon"], Is.EqualTo("sync"));
            Assert.That((string?)owned["Url"], Is.EqualTo("/jellyfin/AniDoki/settings"));
            Assert.That((string?)pages.Single(page => (string?)page["Id"] == "other.plugin")["Icon"], Is.EqualTo("star"));
            Assert.That((bool)result["keep"]!, Is.True);
        });
        plugin.CheckPluginPages(_paths.Object, CreateServerConfiguration());
        Assert.That((JArray)JObject.Parse(File.ReadAllText(_pagesPath))["pages"]!, Has.Count.EqualTo(2));
    }

    private static IServerConfigurationManager CreateServerConfiguration()
    {
        var manager = new Mock<IServerConfigurationManager>();
        manager.Setup(configuration => configuration.GetConfiguration("network"))
            .Returns(new NetworkConfiguration { BaseUrl = "/jellyfin" });
        return manager.Object;
    }

    [Test]
    public void OnlyTheAdminPageIsEnabledInTheDashboardMenu()
    {
        var plugin = CreatePlugin();
        var visible = plugin.GetPages().Where(page => page.EnableInMainMenu).ToArray();
        Assert.That(visible.Length, Is.EqualTo(1));
        Assert.That(visible[0].Name, Is.EqualTo("AniDoki"));
        Assert.That(visible[0].DisplayName, Is.EqualTo("AniDōki"));
        Assert.That(visible[0].MenuIcon, Is.EqualTo("sync"));
        Assert.That(visible[0].EmbeddedResourcePath,
            Is.EqualTo("jellyfin_anidoki.Configuration.ConfigPage.html"));
    }
}
