using System.Text.Json;
using System.Text.Json.Serialization;

namespace jellyfin_anidoki.Operations;

/// <summary>The stored form of planned changes and snapshots. Enums are names, so a reordered enum cannot change old rows.</summary>
internal static class ActionJson {
    private static readonly JsonSerializerOptions Options = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
