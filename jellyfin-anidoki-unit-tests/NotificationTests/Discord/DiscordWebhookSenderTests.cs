using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Notifications.Discord;
using Moq;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.NotificationTests.Discord;

[TestFixture]
public class DiscordWebhookSenderTests {
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; Assert.That(request.RequestUri!.Query, Is.EqualTo("?wait=true")); return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }); }
    }
    [TestCase(HttpStatusCode.OK, "{\"id\":\"123456789012345678\"}", DiscordDeliveryKind.Delivered)]
    [TestCase(HttpStatusCode.OK, "{}", DiscordDeliveryKind.Unconfirmed)]
    [TestCase(HttpStatusCode.OK, "{\"id\":\"not-a-message\"}", DiscordDeliveryKind.Unconfirmed)]
    [TestCase(HttpStatusCode.InternalServerError, "{}", DiscordDeliveryKind.Unconfirmed)]
    [TestCase(HttpStatusCode.Unauthorized, "{}", DiscordDeliveryKind.Rejected)]
    [TestCase(HttpStatusCode.TooManyRequests, "{\"retry_after\":0.25,\"global\":true}", DiscordDeliveryKind.RateLimited)]
    public async Task ClassifiesReceiptWithoutResendingAmbiguousRequests(HttpStatusCode status, string body, DiscordDeliveryKind expected) {
        var handler = new Handler(status, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AniDokiDiscord")).Returns(new HttpClient(handler));
        var sender = new DiscordWebhookSender(factory.Object);
        var result = await sender.Send("https://discord.com/api/v10/webhooks/123456789012345678/fixture", new { username = "AniDoki" }, null, CancellationToken.None);
        Assert.That(result.Kind, Is.EqualTo(expected));
        Assert.That(handler.Calls, Is.EqualTo(1));
        if (expected == DiscordDeliveryKind.RateLimited) { Assert.That(result.Delay, Is.EqualTo(TimeSpan.FromSeconds(.25))); Assert.That(result.Global, Is.True); }
    }
}
