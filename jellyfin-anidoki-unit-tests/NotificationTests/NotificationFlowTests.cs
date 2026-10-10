using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

[TestFixture, NonParallelizable]
public class NotificationFlowTests
{
    private string _directory = null!;
    private Plugin _plugin = null!;
    private readonly Guid _user = Guid.NewGuid();
    private Mock<ISessionManager> _sessions = null!;
    private NotificationEventStore _store = null!;
    private NotificationController _controller = null!;
    [SetUp] public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-notifications-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.UserConfig = [new UserConfig { UserId = _user, ShowLogNotifications = true }];
        _sessions = new(); _store = new();
        var user = new User("fixture", "auth", "reset") { Id = _user };
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpRequest>())).ReturnsAsync(new AuthorizationInfo { User = user, Token = "private-fixture-token", DeviceId = "device" });
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(_user)).Returns(user);
        var session = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "session", UserId = _user, DeviceId = "device" };
        _sessions.Setup(s => s.GetSessionByAuthenticationToken("private-fixture-token", null!, It.IsAny<string>())).ReturnsAsync(session);
        _controller = new(_store, authorization.Object, _sessions.Object, users.Object) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
    }
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);
    private PlaybackNotificationContext Context() => new(Guid.NewGuid(), _user, "session", Guid.NewGuid(), "Anime", 3, false);
    private async Task<PlaybackOutcomeCollector> ObservePartial()
    {
        var collector = new PlaybackOutcomeCollector(); var target = new Anime { Id = 1, Title = "Anime" }; int calls = 0;
        await collector.ObserveWrite(ApiName.AniList, target, 3, Status.Watching, "progress", () => { calls++; return Task.FromResult<UpdateAnimeStatusResponse?>(new() { UsesAcknowledgementFields = true, AcknowledgedProgress = 3, AcknowledgedStatus = Status.Watching }); });
        await collector.ObserveWrite(ApiName.Mal, target, 3, Status.Watching, "progress", () => { calls++; return Task.FromResult<UpdateAnimeStatusResponse?>(null); });
        Assert.That(calls, Is.EqualTo(2)); return collector;
    }
    [Test] public async Task ObservedReceiptBecomesOneOwnSessionPublicEventWithoutCredentials()
    {
        var tail = (await _controller.Read()).Value!.Cursor;
        var collector = await ObservePartial(); var context = Context();
        PlaybackNotificationPublisher.Publish(collector, context, _user, _store, NullLogger.Instance);
        PlaybackNotificationPublisher.Publish(collector, context, _user, _store, NullLogger.Instance);
        var response = (await _controller.Read(tail)).Value!;
        Assert.That(response.Events, Has.Length.EqualTo(1)); Assert.That(response.Events[0].Outcomes, Has.Length.EqualTo(2));
        Assert.That(response.Events[0].Outcomes[0].Progress, Is.EqualTo(3));
        Assert.That(response.Events[0].Outcomes[1].Kind, Is.EqualTo("Unconfirmed"));
        Assert.That(_store.Read(_user, "other-session", tail).Events, Is.Empty);
        var json = JsonSerializer.Serialize(response);
        Assert.That(json, Does.Not.Contain("private-fixture-token").And.Not.Contain("sessionId").And.Not.Contain("userId"));
    }
    [Test] public async Task DisabledPreferenceSuppressesPublicationAndReadsAndReenableStartsAtTail()
    {
        var tail = (await _controller.Read()).Value!.Cursor;
        var collector = await ObservePartial();
        PlaybackNotificationPublisher.Publish(collector, Context(), _user, _store, NullLogger.Instance);
        _plugin.Configuration.UserConfig[0].ShowLogNotifications = false;
        var disabled = (await _controller.Read(tail)).Value!;
        Assert.That(disabled.Enabled, Is.False); Assert.That(disabled.Events, Is.Empty);
        PlaybackNotificationPublisher.Publish(collector, Context(), _user, _store, NullLogger.Instance);
        Assert.That(_store.Count, Is.EqualTo(1));
        _plugin.Configuration.UserConfig[0].ShowLogNotifications = true;
        Assert.That((await _controller.Read(disabled.Cursor)).Value!.Events, Is.Empty);
    }
    [Test] public async Task HostLifecycleSubscribesOnceAndUnsubscribesOnStop()
    {
        var host = new SessionServerEntry(_sessions.Object, NullLoggerFactory.Instance, new Mock<IHttpClientFactory>().Object,
            new Mock<ILibraryManager>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(),
            new Mock<IApplicationPaths>().Object, new Mock<IMemoryCache>().Object, _store);
        await host.StartAsync(CancellationToken.None); await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None); await host.StopAsync(CancellationToken.None);
        _sessions.VerifyAdd(s => s.PlaybackStopped += It.IsAny<EventHandler<PlaybackStopEventArgs>>(), Times.Once);
        _sessions.VerifyRemove(s => s.PlaybackStopped -= It.IsAny<EventHandler<PlaybackStopEventArgs>>(), Times.Once);
    }
}
