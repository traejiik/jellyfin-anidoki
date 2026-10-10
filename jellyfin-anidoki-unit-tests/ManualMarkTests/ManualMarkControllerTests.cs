using System;
using System.IO;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkControllerTests
{
    private string _directory = null!;
    private ManualMarkStore _store = null!;
    private ManualMarkController _controller = null!;
    private ManualMarkRequest _request = null!;
    private ManualMarkQueue _queue = null!;
    [SetUp]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "manual-controller-" + Guid.NewGuid()); Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.PluginsPath).Returns(_directory); paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>(); xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration { watchedTickboxUpdatesProvider = true });
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        var user = new User("fixture", "auth", "reset") { Id = Guid.NewGuid() };
        var auth = new Mock<IAuthorizationContext>(); auth.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpRequest>())).ReturnsAsync(new AuthorizationInfo { User = user, Token = "fixture-token", DeviceId = "own-device" });
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var sessions = new Mock<ISessionManager>(); sessions.Setup(s => s.GetSessionByAuthenticationToken("fixture-token", null!, It.IsAny<string>())).ReturnsAsync(new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "own-session", UserId = user.Id, DeviceId = "own-device" });
        var resolver = new AuthenticatedSessionResolver(auth.Object, sessions.Object, users.Object);
        var context = new DefaultHttpContext(); var identity = (await resolver.Resolve(context)).Identity!;
        _store = new(TimeProvider.System); _queue = new();
        _controller = new(_store, new ManualMarkCapabilityRegistry(Path.Combine(_directory, "enrollment.json")), resolver, _queue) { ControllerContext = new() { HttpContext = context } };
        _request = new(Guid.NewGuid(), user.Id, identity.AuthenticationKey, Guid.NewGuid(), "Anime", 3, "Episode", DateTimeOffset.UtcNow, "account");
    }
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);
    [Test]
    public async Task ConfirmQueuesOnceAndRepeatedConfirmReturnsCurrentState()
    {
        var pending = _store.CreateOrGet(_request)!;
        Assert.That((await _controller.Confirm(pending.OperationId, new(pending.Revision))).Result, Is.TypeOf<AcceptedResult>());
        Assert.That((await _controller.Confirm(pending.OperationId, new(pending.Revision))).Result, Is.TypeOf<OkObjectResult>());
        Assert.That(_queue.TryRead(out var id), Is.True); Assert.That(id, Is.EqualTo(pending.OperationId));
        Assert.That(_queue.TryRead(out _), Is.False);
    }
    [Test]
    public async Task ForeignOrStaleCommandNeverQueues()
    {
        var pending = _store.CreateOrGet(_request)!;
        Assert.That((await _controller.Confirm(Guid.NewGuid(), new(pending.Revision))).Result, Is.TypeOf<NotFoundResult>());
        Assert.That(((ObjectResult)(await _controller.Confirm(pending.OperationId, new(pending.Revision + 1))).Result!).StatusCode, Is.EqualTo(409));
        Assert.That(_queue.TryRead(out _), Is.False);
    }
    [Test]
    public async Task DisabledAdministratorSwitchCancelsPendingWork()
    {
        var pending = _store.CreateOrGet(_request)!; Plugin.Instance!.Configuration.watchedTickboxUpdatesProvider = false;
        Assert.That(((ObjectResult)(await _controller.Confirm(pending.OperationId, new(pending.Revision))).Result!).StatusCode, Is.EqualTo(409));
        Assert.That(_store.Read(_request.UserId, _request.AuthenticationKey).Length, Is.EqualTo(1));
        Assert.That(_queue.TryRead(out _), Is.False);
    }
}
