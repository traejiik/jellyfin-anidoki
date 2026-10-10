#nullable enable
using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace jellyfin_anidoki.Notifications.Discord;

public enum DiscordDeliveryKind { Delivered, RateLimited, Rejected, Unconfirmed, Cancelled }
public sealed record DiscordDeliveryResult(DiscordDeliveryKind Kind, TimeSpan Delay, bool Global = false)
{
    public string Message => Kind switch { DiscordDeliveryKind.Delivered => "Discord confirmed delivery.", DiscordDeliveryKind.Rejected => "Discord rejected this webhook. Replace it or send a test before resuming.", DiscordDeliveryKind.RateLimited => "Discord rate limited delivery.", DiscordDeliveryKind.Cancelled => "Delivery cancelled because settings, access or the deadline changed.", _ => "Discord delivery could not be confirmed. It will not be resent automatically." };
}
public sealed class DiscordWebhookSender(IHttpClientFactory clients)
{
    public async Task<DiscordDeliveryResult> Send(string webhook, object payload, byte[]? poster, CancellationToken cancellation)
    {
        var canonical = DiscordWebhookAddress.Parse(webhook); if (canonical == null) return new(DiscordDeliveryKind.Rejected, TimeSpan.Zero);
        try
        {
            using var client = clients.CreateClient("AniDokiDiscord");
            using var request = new HttpRequestMessage(HttpMethod.Post, canonical + "?wait=true");
            if (poster != null) { var multipart = new MultipartFormDataContent(); multipart.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json"); var image = new ByteArrayContent(poster); image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"); multipart.Add(image, "files[0]", "poster.jpg"); request.Content = multipart; }
            else request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return new(DiscordDeliveryKind.Rejected, TimeSpan.Zero);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests) return new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero);
            if (response.Content.Headers.ContentLength > 8192) return new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero);
            using var stream = await response.Content.ReadAsStreamAsync(cancellation); var buffer = new byte[8193]; var count = await stream.ReadAtLeastAsync(buffer, 8193, false, cancellation);
            if (count > 8192) return new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (json.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds <= 86400)
                    return new(DiscordDeliveryKind.RateLimited, TimeSpan.FromSeconds(seconds), json.RootElement.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.True);
                return new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero);
            }
            return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Regex.IsMatch(id.GetString() ?? "", "^[0-9]{17,20}$") ? new(DiscordDeliveryKind.Delivered, TimeSpan.Zero) : new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero);
        }
        catch (Exception) { return new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero); }
    }
}
