using System;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Helpers;

/// <summary>
/// Sign-in state keys: a random key, then a short marker of the issuing server and the provider,
/// so a callback that finds no stored state can tell "expired here" from "issued elsewhere".
/// Neither marker is secret: the server ID is public and the provider is in the authorize URL.
/// </summary>
public static class CallbackState {
    internal const int ServerMarkerLength = 8;

    public static string Create(string? serverId, ApiName provider) {
        string key = Guid.NewGuid().ToString("N");
        string? marker = ServerMarker(serverId);
        return marker == null ? key : $"{key}-{marker}-{provider}";
    }

    /// <summary>Reads the markers of a state this or another AniDōki server issued.</summary>
    public static bool TryReadOrigin(string? state, out string serverMarker, out ApiName? provider) {
        serverMarker = "";
        provider = null;
        string[] parts = (state ?? "").Split('-');
        if (parts.Length != 3 || parts[0].Length != 32 || parts[1].Length != ServerMarkerLength)
            return false;
        foreach (char c in parts[1]) {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        serverMarker = parts[1].ToLowerInvariant();
        if (Enum.TryParse(parts[2], ignoreCase: false, out ApiName parsed) && Enum.IsDefined(parsed))
            provider = parsed;
        return true;
    }

    public static bool IsFromOtherServer(string? state, string? serverId) =>
        TryReadOrigin(state, out string marker, out _) && ServerMarker(serverId) is { } own && marker != own;

    internal static string? ServerMarker(string? serverId) {
        string hex = (serverId ?? "").Replace("-", "").ToLowerInvariant();
        if (hex.Length < ServerMarkerLength)
            return null;
        hex = hex[..ServerMarkerLength];
        foreach (char c in hex) {
            if (!Uri.IsHexDigit(c))
                return null;
        }
        return hex;
    }
}
