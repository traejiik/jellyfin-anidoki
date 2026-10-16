#nullable enable
using System;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models;
namespace jellyfin_anidoki.Notifications.Discord;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscordWebhookAction { Keep, Replace, Clear }
public sealed record DiscordSettingsCommand(bool Enabled, DiscordWebhookAction WebhookAction, string? Webhook, long ExpectedRevision);
public sealed record DiscordSettingsResponse(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hasWebhook")] bool HasWebhook,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("publicAddressReady")] bool PublicAddressReady,
    [property: JsonPropertyName("deliveryStatus")] string? DeliveryStatus);
public sealed class DiscordSettingsService {
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (long Revision, string Status)> _health = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _blocked = new();
    internal static bool AddressReady {
        get { var address = Plugin.Instance?.PluginConfiguration.callbackUrl; if (string.IsNullOrWhiteSpace(address)) return false; try { CallbackUrlHelper.Build(address); return true; } catch (ArgumentException) { return false; } }
    }
    public DiscordSettingsResponse Read(Guid userId) {
        var configuration = Plugin.Instance?.PluginConfiguration;
        if (configuration == null)
            return new(false, false, 0, false, null);
        lock (configuration) {
            var user = configuration.UserConfig?.FirstOrDefault(u => u.UserId == userId);
            return new(user?.DiscordNotificationsEnabled ?? false, !string.IsNullOrEmpty(user?.DiscordWebhookUrl), user?.DiscordNotificationRevision ?? 0, AddressReady,
                _health.TryGetValue(userId, out var health) && health.Revision == user?.DiscordNotificationRevision ? health.Status : null);
        }
    }
    public (int Status, DiscordSettingsResponse? Settings, string? Error) Save(Guid userId, DiscordSettingsCommand command) {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return (503, null, "Plugin configuration unavailable.");
        lock (plugin.PluginConfiguration) {
            var user = plugin.PluginConfiguration.UserConfig?.FirstOrDefault(u => u.UserId == userId);
            if ((user?.DiscordNotificationRevision ?? 0) != command.ExpectedRevision)
                return (409, Read(userId), "Discord settings changed. Reload before saving.");
            if (!Enum.IsDefined(command.WebhookAction))
                return (400, null, "Invalid webhook action.");
            var webhook = command.WebhookAction switch { DiscordWebhookAction.Keep => user?.DiscordWebhookUrl, DiscordWebhookAction.Clear => null, _ => DiscordWebhookAddress.Parse(command.Webhook) };
            if (command.WebhookAction == DiscordWebhookAction.Replace && webhook == null)
                return (400, null, "Enter a valid Discord incoming webhook link.");
            var enabled = command.WebhookAction != DiscordWebhookAction.Clear && command.Enabled;
            if (enabled && (webhook == null || !AddressReady))
                return (422, null, "Save a webhook and ask your administrator to set the server Public address.");
            var originalUsers = plugin.PluginConfiguration.UserConfig;
            if (user == null) { user = new UserConfig { UserId = userId }; plugin.PluginConfiguration.UserConfig = (plugin.PluginConfiguration.UserConfig ?? []).Append(user).ToArray(); }
            if (user.DiscordWebhookUrl == webhook && user.DiscordNotificationsEnabled == enabled)
                return (200, Read(userId), null);
            var previous = (user.DiscordWebhookUrl, user.DiscordNotificationsEnabled, user.DiscordNotificationRevision);
            user.DiscordWebhookUrl = webhook;
            user.DiscordNotificationsEnabled = enabled;
            user.DiscordNotificationRevision++;
            try { plugin.SaveConfiguration(); } catch (Exception) {
                (user.DiscordWebhookUrl, user.DiscordNotificationsEnabled, user.DiscordNotificationRevision) = previous;
                plugin.PluginConfiguration.UserConfig = originalUsers;
                return (503, Read(userId), "Discord settings could not be saved. No new destination was enabled.");
            }
            _health.TryRemove(userId, out _);
            return (200, Read(userId), null);
        }
    }
    internal (string Webhook, string Address)? Destination(Guid userId, long revision, bool test = false) {
        var configuration = Plugin.Instance?.PluginConfiguration;
        if (configuration == null)
            return null;
        lock (configuration) {
            var user = configuration.UserConfig?.FirstOrDefault(u => u.UserId == userId);
            if (user == null || user.DiscordNotificationRevision != revision || (!test && (!user.DiscordNotificationsEnabled || _blocked.TryGetValue(userId, out var blocked) && blocked == revision)) || !AddressReady)
                return null;
            var url = DiscordWebhookAddress.Parse(user.DiscordWebhookUrl);
            return url == null ? null : (url, configuration.callbackUrl);
        }
    }
    internal void Block(Guid user, long revision) => _blocked[user] = revision;
    internal void Unblock(Guid user, long revision) { if (_blocked.TryGetValue(user, out var blocked) && blocked == revision) _blocked.TryRemove(user, out _); }
    internal void Health(Guid user, long revision, string status) => _health[user] = (revision, status);
}
