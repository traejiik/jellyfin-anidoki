#nullable enable
using System;

namespace jellyfin_anidoki.Helpers;

public class ProviderHelpers {
    public static string GetShikimoriDomain() {
        if (!string.IsNullOrWhiteSpace(Plugin.Instance?.PluginConfiguration.shikimoriDomain) &&
            Uri.TryCreate(Plugin.Instance.PluginConfiguration.shikimoriDomain, UriKind.Absolute, out Uri? baseDomain)) {
            return baseDomain.AbsoluteUri;
        }

        return "https://shikimori.io/";
    }
}
