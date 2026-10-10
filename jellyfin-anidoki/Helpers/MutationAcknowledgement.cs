#nullable enable
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace jellyfin_anidoki.Helpers;

// Validate the receipt already returned by a mutation. Never issue a verification request.
internal static class MutationAcknowledgement {
    internal static async Task<bool> Validate(HttpResponseMessage? response, Func<JsonElement, bool> expected) {
        if (response is not { IsSuccessStatusCode: true }) return false;
        try {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !HasErrors(root) && expected(root);
        } catch (JsonException) {
            return false;
        } catch (InvalidOperationException) {
            return false;
        }
    }

    private static bool HasErrors(JsonElement root) {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return true;
        return root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null &&
               (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0);
    }

    internal static bool Object(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    internal static bool Number(JsonElement parent, string name, out int value) {
        value = 0;
        return parent.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }
    internal static bool Text(JsonElement parent, string name, string value) =>
        parent.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == value;
    internal static bool Id(JsonElement parent) => parent.TryGetProperty("id", out var id) &&
        ((id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0) ||
         (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())));
}
