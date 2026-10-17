using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

/// <summary>Replaces or clears one provider's client secret. The secret is never returned.</summary>
public sealed class ProviderSecretRequest {
    public ApiName Provider { get; set; }

    public string? Secret { get; set; }

    public bool Clear { get; set; }
}

public sealed record ProviderSecretState(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("hasSecret")] bool HasSecret);

public sealed record StorageState(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("message")] string? Message);
