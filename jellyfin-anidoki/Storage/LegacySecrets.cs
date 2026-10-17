using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Storage;

/// <summary>
/// Reads credentials that older plugin versions wrote into the plugin XML. The configuration model now ignores those
/// elements, so they are read separately on startup before being moved into the database.
/// </summary>
internal static class LegacySecrets {
    private static readonly XmlSerializer Serializer = new(typeof(LegacySecretDocument));

    /// <exception cref="InvalidOperationException">The file exists but is not readable configuration XML.</exception>
    public static Dictionary<SecretKey, string> Read(string configurationPath) {
        var secrets = new Dictionary<SecretKey, string>();
        if (!File.Exists(configurationPath))
            return secrets;
        LegacySecretDocument? document;
        using (var stream = File.OpenRead(configurationPath))
            document = (LegacySecretDocument?)Serializer.Deserialize(stream);
        foreach (var provider in document?.Providers ?? [])
            SecretOverlay.Add(secrets, new SecretKey(SecretKey.ClientSecretKind, "", provider.Name.ToString()), provider.ClientSecret);
        foreach (var user in document?.Users ?? []) {
            string owner = user.UserId.ToString("N");
            SecretOverlay.Add(secrets, new SecretKey(SecretKey.DiscordWebhookKind, owner, ""), user.DiscordWebhookUrl);
            foreach (var account in user.Accounts ?? []) {
                SecretOverlay.Add(secrets, new SecretKey(SecretKey.AccessTokenKind, owner, account.Name.ToString()), account.AccessToken);
                SecretOverlay.Add(secrets, new SecretKey(SecretKey.RefreshTokenKind, owner, account.Name.ToString()), account.RefreshToken);
            }
        }
        return secrets;
    }
}

// XmlSerializer needs public types. These mirror only the credential elements of the plugin XML; everything else is skipped.

[XmlRoot("PluginConfiguration")]
public sealed class LegacySecretDocument {
    [XmlArray("UserConfig"), XmlArrayItem("UserConfig")]
    public LegacyUserSecrets[]? Users { get; set; }

    [XmlArray("ProviderApiAuth"), XmlArrayItem("ProviderApiAuth")]
    public LegacyProviderSecrets[]? Providers { get; set; }
}

public sealed class LegacyUserSecrets {
    public Guid UserId { get; set; }

    public string? DiscordWebhookUrl { get; set; }

    [XmlArray("UserApiAuth"), XmlArrayItem("UserApiAuth")]
    public LegacyAccountSecrets[]? Accounts { get; set; }
}

public sealed class LegacyAccountSecrets {
    public ApiName Name { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }
}

public sealed class LegacyProviderSecrets {
    public ApiName Name { get; set; }

    public string? ClientSecret { get; set; }
}
