using System;
using System.IO;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkTrackingTests {
    private string _directory = null!;
    private Mock<ILibraryManager> _library = null!;
    private Guid _user;
    private Guid _libraryId;

    [SetUp]
    public void Setup() {
        _directory = Path.Combine(Path.GetTempPath(), "manual-tracking-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration { watchedTickboxUpdatesProvider = true });
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _user = Guid.NewGuid();
        _libraryId = Guid.NewGuid();
        _library = new Mock<ILibraryManager>();
        _library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = _libraryId.ToString(), Name = "Anime", Locations = ["/media/anime"] }]);
        BaseItem.LibraryManager = _library.Object;
    }
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);

    private void Configure(params UserConfig[] users) => Plugin.Instance!.Configuration.UserConfig = users;

    [Test]
    public void UserWithoutSettingsIsNotTracked() {
        Configure();
        Assert.That(ManualMarkRequestFilter.IsTracked(_user, new Episode(), _library.Object, NullLogger.Instance), Is.False);
    }
    [Test]
    public void AnotherUsersSettingsDoNotTrackThisUser() {
        Configure(new UserConfig { UserId = Guid.NewGuid(), LibraryToCheck = [] });
        Assert.That(ManualMarkRequestFilter.IsTracked(_user, new Episode(), _library.Object, NullLogger.Instance), Is.False);
    }
    [Test]
    public void UserWithoutLibraryFilterIsTrackedEverywhere() {
        Configure(new UserConfig { UserId = _user, LibraryToCheck = [] });
        Assert.That(ManualMarkRequestFilter.IsTracked(_user, new Episode(), _library.Object, NullLogger.Instance), Is.True);
    }
    [Test]
    public void ItemOutsideSelectedLibrariesIsNotTracked() {
        Configure(new UserConfig { UserId = _user, LibraryToCheck = [_libraryId.ToString()] });
        Assert.That(ManualMarkRequestFilter.IsTracked(_user, new Episode(), _library.Object, NullLogger.Instance), Is.False);
    }
}
