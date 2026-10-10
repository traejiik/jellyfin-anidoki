using System;
using System.Net;
using System.Threading.Tasks;
using jellyfin_anidoki.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

[TestFixture]
public class NotificationControllerTests
{
    private Mock<IAuthorizationContext> _auth = null!;
    private Mock<ISessionManager> _sessions = null!;
    private Mock<IUserManager> _users = null!;
    private AuthorizationInfo _info = null!;
    private SessionInfo _session = null!;
    private NotificationController _controller = null!;
    [SetUp] public void Setup()
    {
        _auth = new(); _sessions = new(); _users = new();
        _info = new() { User = new User("fixture", "auth", "reset") { Id = Guid.NewGuid() }, Token = "fixture-token", DeviceId = "own-device" };
        _session = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "own-session", UserId = _info.UserId, DeviceId = "own-device" };
        _auth.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpRequest>())).ReturnsAsync(() => _info);
        _users.Setup(u => u.GetUserById(_info.UserId)).Returns(new User("fixture", "auth", "reset") { Id = _info.UserId });
        _sessions.Setup(s => s.GetSessionByAuthenticationToken("fixture-token", null!, It.IsAny<string>())).ReturnsAsync(() => _session);
        _controller = new(new NotificationEventStore(), new AuthenticatedSessionResolver(_auth.Object, _sessions.Object, _users.Object)) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        _controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
    }
    [Test] public async Task AuthenticationFingerprintIsStableButChangesWithTokenOrDevice()
    {
        var resolver = new AuthenticatedSessionResolver(_auth.Object, _sessions.Object, _users.Object);
        var first = await resolver.Resolve(_controller.HttpContext);
        var again = await resolver.Resolve(_controller.HttpContext);
        Assert.That(first.Identity!.AuthenticationKey, Is.EqualTo(again.Identity!.AuthenticationKey));
        Assert.That(first.Identity.AuthenticationKey, Does.Not.Contain("fixture-token"));
        _info.Token = "rotated-token";
        _sessions.Setup(s => s.GetSessionByAuthenticationToken("rotated-token", null!, It.IsAny<string>())).ReturnsAsync(() => _session);
        var rotated = await resolver.Resolve(_controller.HttpContext);
        Assert.That(rotated.Identity!.AuthenticationKey, Is.Not.EqualTo(first.Identity.AuthenticationKey));
        _info.DeviceId = _session.DeviceId = "rotated-device";
        Assert.That((await resolver.Resolve(_controller.HttpContext)).Identity!.AuthenticationKey, Is.Not.EqualTo(rotated.Identity.AuthenticationKey));
    }
    [Test] public async Task OwnTokenBoundSessionGetsUncachedTail()
    {
        var result = await _controller.Read(); Assert.That(result.Value, Is.Not.Null);
        Assert.That(result.Value!.Events, Is.Empty); Assert.That(_controller.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        _sessions.Verify(s => s.GetSessionByAuthenticationToken("fixture-token", null!, "127.0.0.1"), Times.Once);
    }
    [TestCase("apikey"), TestCase("nouser"), TestCase("notoken"), TestCase("deleted")]
    public async Task InvalidCallerRejectedBeforeSessionLookup(string kind)
    {
        if (kind == "apikey") _info.IsApiKey = true;
        if (kind == "nouser") _info.User = null!;
        if (kind == "notoken") _info.Token = "";
        if (kind == "deleted") _users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((User)null!);
        Assert.That((await _controller.Read()).Result, Is.TypeOf<UnauthorizedResult>());
        _sessions.Verify(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
    [TestCase("foreignuser"), TestCase("forgeddevice"), TestCase("nosession"), TestCase("emptydevice"), TestCase("emptysession")]
    public async Task ForeignOrMissingSessionCannotBeSelected(string kind)
    {
        if (kind == "foreignuser") _session.UserId = Guid.NewGuid();
        if (kind == "forgeddevice") _info.DeviceId = "other-device";
        if (kind == "nosession") _session = null!;
        if (kind == "emptydevice") _info.DeviceId = "";
        if (kind == "emptysession") _session.Id = "";
        Assert.That((await _controller.Read("forged:0")).Result, Is.TypeOf<ForbidResult>());
    }
}
