using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace jellyfin_anidoki.Configuration;

/// <summary>
/// Reads and writes the Plugin Pages plugin's config.json, which AniDoki edits in place
/// alongside other plugins' registrations.
/// </summary>
internal static class PluginPagesJson {
    // Duplicate keys are rejected as malformed; JsonObject would otherwise throw an
    // ArgumentException on first access, which callers do not expect from a parse.
    private static readonly JsonDocumentOptions ReadOptions = new() {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false
    };

    // Keep the file readable for admins: indented, and "AniDōki" left unescaped.
    private static readonly JsonSerializerOptions WriteOptions = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static JsonObject Parse(string json) =>
        JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject ??
        throw new JsonException("Plugin Pages configuration is not a JSON object.");

    internal static string Serialize(JsonObject config) => config.ToJsonString(WriteOptions);

    internal static bool IsString(JsonNode? node, out string value) {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String) {
            value = jsonValue.GetValue<string>();
            return true;
        }
        value = "";
        return false;
    }
}
