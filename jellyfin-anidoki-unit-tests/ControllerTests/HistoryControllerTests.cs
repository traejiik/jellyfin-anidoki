using System;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Operations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

/// <summary>D7: who may read whose history, last update and operations.</summary>
[TestFixture, NonParallelizable]
public class HistoryControllerTests {
    private string _directory = null!;
    private Plugin _plugin = null!;
    private OperationStore _store = null!;
    private HistoryReader _history = null!;
    private AccountAttention _attention = null!;
    private Mock<IUserManager> _users = null!;
    private User _alice = null!;
    private User _bob = null!;
    private User _admin = null!;

    [SetUp]
    public void SetUp() {
        _directory = OperationTestSupport.NewDirectory();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(item => item.PluginsPath).Returns(_directory);
        paths.SetupGet(item => item.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(item => item.DataPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(serializer => serializer.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.enableUserPages = true;

        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _store = new OperationStore(database, TimeProvider.System);
        _history = new HistoryReader(database, TimeProvider.System);
        _attention = new AccountAttention(database);
        _alice = new User("alice", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _bob = new User("bob", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _admin = new User("admin", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _admin.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        _users = new Mock<IUserManager>();
        foreach (var user in new[] { _alice, _bob, _admin })
            _users.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private HistoryController As(User user) => new(_history, _attention, _store, _users.Object) {
        ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimValues.UserId, user.Id.ToString())])) }
        }
    };

    // Forbid() carries no status of its own; Jellyfin's authentication answers it with 403.
    private static int? Status<T>(ActionResult<T> result) => result.Result switch {
        ForbidResult => 403,
        IStatusCodeActionResult status => status.StatusCode,
        _ => null
    };

    private Guid Recorded(User user) {
        Guid id = Guid.NewGuid();
        _store.Create(id, user.Id, OperationKind.Playback, "Fixture Show · episode 5");
        _store.Finish(id, OperationState.NoChange, null);
        return id;
    }

    [Test]
    public void AUserReadsTheirOwnHistory() {
        Recorded(_alice);

        var result = As(_alice).ReadOwnHistory(_alice.Id);

        Assert.That(Status(result), Is.EqualTo(200));
        Assert.That(((HistoryPage)((ObjectResult)result.Result!).Value!).Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void AUserCannotReadAnotherUsersHistoryActivityOrOperation() {
        Guid bobs = Recorded(_bob);
        var alice = As(_alice);

        Assert.That(Status(alice.ReadOwnHistory(_bob.Id)), Is.EqualTo(403));
        Assert.That(Status(alice.ReadOwnActivity(_bob.Id)), Is.EqualTo(403));
        Assert.That(Status(alice.ReadOwnOperation(bobs)), Is.EqualTo(403));
    }

    [Test]
    public void AnAdminCanReadAnyonesHistoryAndOperations() {
        Guid bobs = Recorded(_bob);
        var admin = As(_admin);

        Assert.That(Status(admin.ReadOwnHistory(_bob.Id)), Is.EqualTo(200));
        Assert.That(Status(admin.ReadOwnOperation(bobs)), Is.EqualTo(200));
        Assert.That(Status(admin.ReadHistory(_bob.Id)), Is.EqualTo(200));
        Assert.That(Status(admin.ReadActivity()), Is.EqualTo(200));
    }

    [Test]
    public void AUserReadsTheirOwnOperation() {
        Guid own = Recorded(_alice);

        Assert.That(Status(As(_alice).ReadOwnOperation(own)), Is.EqualTo(200));
        Assert.That(Status(As(_alice).ReadOwnOperation(Guid.NewGuid())), Is.EqualTo(404));
    }

    [Test]
    public void TheUserRoutesAreOffWithoutUserPages() {
        _plugin.Configuration.enableUserPages = false;

        Assert.That(Status(As(_alice).ReadOwnHistory(_alice.Id)), Is.EqualTo(404));
        Assert.That(Status(As(_alice).ReadOwnActivity(_alice.Id)), Is.EqualTo(404));
    }

    [Test]
    public void UnavailableStorageAnswers503SoThePageCanSaySo() {
        File.WriteAllText(Path.Combine(_directory, "broken.db"), "not a database");
        var broken = new AniDokiDatabase(Path.Combine(_directory, "broken.db"));
        var controller = new HistoryController(new HistoryReader(broken, TimeProvider.System), new AccountAttention(broken),
            new OperationStore(broken, TimeProvider.System), _users.Object) { ControllerContext = As(_alice).ControllerContext };

        var result = controller.ReadOwnHistory(_alice.Id);

        Assert.That(Status(result), Is.EqualTo(503));
        Assert.That(((ObjectResult)result.Result!).Value, Is.InstanceOf<StorageUnavailable>());
    }

    [TestCase(nameof(HistoryController.ReadHistory))]
    [TestCase(nameof(HistoryController.ReadActivity))]
    public void TheAdminRoutesRequireElevation(string action) =>
        Assert.That(typeof(HistoryController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()?.Policy, Is.EqualTo(Policies.RequiresElevation));

    [TestCase(nameof(HistoryController.ReadOwnHistory))]
    [TestCase(nameof(HistoryController.ReadOwnActivity))]
    [TestCase(nameof(HistoryController.ReadOwnOperation))]
    public void TheUserRoutesRequireASignedInUser(string action) {
        var authorize = typeof(HistoryController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.That(authorize, Is.Not.Null);
        Assert.That(typeof(HistoryController).GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
    }
}
