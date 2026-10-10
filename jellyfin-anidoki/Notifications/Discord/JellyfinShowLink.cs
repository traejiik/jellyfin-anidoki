using System;
using jellyfin_anidoki.Helpers;
namespace jellyfin_anidoki.Notifications.Discord;

public static class JellyfinShowLink
{
    public static string Build(string baseAddress, Guid showItemId, string serverId)
    {
        CallbackUrlHelper.Build(baseAddress);
        if (showItemId == Guid.Empty || string.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("Show link metadata unavailable.");
        return baseAddress.TrimEnd('/') + "/web/index.html#/details?id=" + showItemId.ToString("N") + "&serverId=" + Uri.EscapeDataString(serverId);
    }
}
