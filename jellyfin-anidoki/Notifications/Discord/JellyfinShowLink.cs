using System;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Trackers.Simkl;
namespace jellyfin_anidoki.Notifications.Discord;

public static class JellyfinShowLink {
    public static string Build(string baseAddress, Guid showItemId, string serverId) {
        CallbackUrlHelper.Build(baseAddress);
        if (showItemId == Guid.Empty || string.IsNullOrWhiteSpace(serverId))
            throw new ArgumentException("Show link metadata unavailable.");
        var id = showItemId.ToString("N");
        return $"{baseAddress.TrimEnd('/')}/web/index.html#/details?id={id}&serverId={Uri.EscapeDataString(serverId)}&anidoki={id}";
    }
}
