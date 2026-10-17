using System.Collections.Generic;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Storage;

/// <summary>
/// Moves credentials between the in-memory configuration and <see cref="SecretKey"/> dictionaries. The configuration
/// keeps credentials in memory for the code that uses them; <c>[XmlIgnore, JsonIgnore]</c> on those properties keeps
/// them out of the XML file and every JSON response.
/// </summary>
internal static class SecretOverlay {
    public static Dictionary<SecretKey, string> Extract(PluginConfiguration configuration) {
        var secrets = new Dictionary<SecretKey, string>();
        foreach (var provider in configuration.ProviderApiAuth ?? [])
            Add(secrets, new SecretKey(SecretKey.ClientSecretKind, "", provider.Name.ToString()), provider.ClientSecret);
        foreach (var user in configuration.UserConfig ?? []) {
            string owner = user.UserId.ToString("N");
            Add(secrets, new SecretKey(SecretKey.DiscordWebhookKind, owner, ""), user.DiscordWebhookUrl);
            foreach (var auth in user.UserApiAuth ?? []) {
                Add(secrets, new SecretKey(SecretKey.AccessTokenKind, owner, auth.Name.ToString()), auth.AccessToken);
                Add(secrets, new SecretKey(SecretKey.RefreshTokenKind, owner, auth.Name.ToString()), auth.RefreshToken);
            }
        }
        return secrets;
    }

    /// <summary>
    /// Sets each credential whose owning record exists in <paramref name="configuration"/>. Values with no matching key
    /// are left as they are; keys with no matching record are ignored.
    /// </summary>
    public static void Apply(PluginConfiguration configuration, IReadOnlyDictionary<SecretKey, string> secrets) {
        foreach (var provider in configuration.ProviderApiAuth ?? []) {
            if (secrets.TryGetValue(new SecretKey(SecretKey.ClientSecretKind, "", provider.Name.ToString()), out var clientSecret))
                provider.ClientSecret = clientSecret;
        }
        foreach (var user in configuration.UserConfig ?? []) {
            string owner = user.UserId.ToString("N");
            if (secrets.TryGetValue(new SecretKey(SecretKey.DiscordWebhookKind, owner, ""), out var webhook))
                user.DiscordWebhookUrl = webhook;
            foreach (var auth in user.UserApiAuth ?? []) {
                if (secrets.TryGetValue(new SecretKey(SecretKey.AccessTokenKind, owner, auth.Name.ToString()), out var access))
                    auth.AccessToken = access;
                if (secrets.TryGetValue(new SecretKey(SecretKey.RefreshTokenKind, owner, auth.Name.ToString()), out var refresh))
                    auth.RefreshToken = refresh;
            }
        }
    }

    internal static void Add(Dictionary<SecretKey, string> secrets, SecretKey key, string? value) {
        if (!string.IsNullOrEmpty(value))
            secrets[key] = value;
    }
}
