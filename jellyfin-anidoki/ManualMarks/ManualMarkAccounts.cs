using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace jellyfin_anidoki.ManualMarks;

internal static class ManualMarkAccounts {
    internal static string Fingerprint(Guid userId) {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return "";
        lock (plugin.PluginConfiguration) {
            var accounts = plugin.PluginConfiguration.UserConfig?.FirstOrDefault(u => u.UserId == userId)?.UserApiAuth?
                .Where(a => !string.IsNullOrWhiteSpace(a.AccessToken)).OrderBy(a => a.Name).ToArray();
            if (accounts == null || accounts.Length == 0)
                return "";
            bool changed = false;
            foreach (var account in accounts)
                if (account.ConnectionGeneration == Guid.Empty) { account.ConnectionGeneration = Guid.NewGuid(); changed = true; }
            if (changed)
                plugin.SaveConfiguration();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", accounts.Select(a => $"{a.Name}:{a.ConnectionGeneration:N}")))));
        }
    }
}
