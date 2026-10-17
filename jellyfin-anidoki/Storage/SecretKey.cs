namespace jellyfin_anidoki.Storage;

/// <summary>
/// Identifies one stored credential. <see cref="Owner"/> is a Jellyfin user ID in "N" format, or empty for server-wide
/// credentials; <see cref="Provider"/> is an <c>ApiName</c>, or empty when the credential has no provider.
/// </summary>
public readonly record struct SecretKey(string Kind, string Owner, string Provider) {
    public const string AccessTokenKind = "access-token";
    public const string RefreshTokenKind = "refresh-token";
    public const string ClientSecretKind = "client-secret";
    public const string DiscordWebhookKind = "discord-webhook";
}
