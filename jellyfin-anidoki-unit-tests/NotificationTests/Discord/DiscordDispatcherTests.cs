using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests.Discord;

[TestFixture, NonParallelizable]
public class DiscordDispatcherTests
{
    private string _directory = null!;
    private Guid _user;
    private DiscordSettingsService _settings = null!;
    private DiscordNotificationDispatcher _dispatcher = null!;
    private Handler _handler = null!;
    private long _revision;
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{\"id\":\"123456789012345678\"}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "discord-dispatcher-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration { callbackUrl = "https://jf.example" });
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _user = Guid.NewGuid();
        _settings = new();
        _revision = _settings.Save(_user, new(true, DiscordWebhookAction.Replace,
            "https://discord.com/api/webhooks/123456789012345678/fixture", 0)).Settings!.Revision;
        _handler = new();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(c => c.CreateClient("AniDokiDiscord")).Returns(() => new HttpClient(_handler, false));
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_user)).Returns(new User("test", "auth", "reset") { Id = _user });
        _dispatcher = new(_settings, new(clients.Object), new(new Mock<IImageProcessor>().Object),
            users.Object, new Mock<ILibraryManager>().Object, new Mock<IServerApplicationHost>().Object, TimeProvider.System);
    }
    [TearDown]
    public async Task Cleanup()
    {
        await _dispatcher.StopAsync(CancellationToken.None);
        _dispatcher.Dispose();
        Directory.Delete(_directory, true);
    }
    [TestCase(HttpStatusCode.OK, DiscordDeliveryKind.Delivered)]
    [TestCase(HttpStatusCode.InternalServerError, DiscordDeliveryKind.Unconfirmed)]
    [TestCase(HttpStatusCode.Forbidden, DiscordDeliveryKind.Rejected)]
    public async Task TerminalReceiptSendsOnceAndNeverRetriesAmbiguousFailure(HttpStatusCode status, DiscordDeliveryKind expected)
    {
        _handler.Status = status;
        var completion = _dispatcher.Test(_user, _revision, null)!;
        await _dispatcher.StartAsync(CancellationToken.None);
        Assert.That((await completion.WaitAsync(TimeSpan.FromSeconds(5))).Kind, Is.EqualTo(expected));
        Assert.That(_handler.Calls, Is.EqualTo(1));
        if (expected == DiscordDeliveryKind.Rejected) Assert.That(_settings.Destination(_user, _revision), Is.Null);
    }
    [Test]
    public async Task OnlyExplicitRateLimitIsRetriedAndStopsAfterTwoRetries()
    {
        _handler.Status = HttpStatusCode.TooManyRequests;
        _handler.Body = "{\"retry_after\":0,\"global\":true}";
        var completion = _dispatcher.Test(_user, _revision, null)!;
        await _dispatcher.StartAsync(CancellationToken.None);
        Assert.That((await completion.WaitAsync(TimeSpan.FromSeconds(5))).Kind, Is.EqualTo(DiscordDeliveryKind.RateLimited));
        Assert.That(_handler.Calls, Is.EqualTo(3));
    }
    [Test]
    public async Task ReplacedWebhookCancelsAlreadyQueuedJobWithoutAnHttpRequest()
    {
        var completion = _dispatcher.Test(_user, _revision, null)!;
        _settings.Save(_user, new(false, DiscordWebhookAction.Clear, null, _revision));
        await _dispatcher.StartAsync(CancellationToken.None);
        Assert.That((await completion.WaitAsync(TimeSpan.FromSeconds(5))).Kind, Is.EqualTo(DiscordDeliveryKind.Cancelled));
        Assert.That(_handler.Calls, Is.Zero);
    }
    [Test]
    public void OnlyMeaningfulConfirmedResultsEnterQueueAndOperationIsDeduplicated()
    {
        var summary = new SyncOperationSummary(Guid.NewGuid(), _user, Guid.NewGuid(), Guid.NewGuid(), "Show", 1, null,
            "Episode", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback, []);
        Assert.That(_dispatcher.Enqueue(summary), Is.False);
        summary = summary with { Outcomes = [new(ApiName.AniList, "1", "Show", OutcomeKind.Confirmed, 1, null, false, "progress", true)] };
        Assert.That(_dispatcher.Enqueue(summary), Is.True);
        Assert.That(_dispatcher.Enqueue(summary), Is.False);
        for (int i = 1; i < 10; i++) Assert.That(_dispatcher.Enqueue(summary with { OperationId = Guid.NewGuid() }), Is.True);
        Assert.That(_dispatcher.Enqueue(summary with { OperationId = Guid.NewGuid() }), Is.False);
    }
}
