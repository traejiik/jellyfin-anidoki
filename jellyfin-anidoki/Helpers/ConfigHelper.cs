using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

public class ConfigHelper {
    public static string? GetShikimoriAppName(ILogger logger) {
        string? shikimoriAppName = Plugin.Instance?.PluginConfiguration.shikimoriAppName;

        if (string.IsNullOrEmpty(shikimoriAppName)) {
            logger.LogWarning("({Provider}) No app name is set; add one on the AniDoki settings page", ApiName.Shikimori);
        }

        return shikimoriAppName;
    }

    public static string? GetSimklClientId(ILogger logger) {
        string? simklClientId = Plugin.Instance?.PluginConfiguration.ProviderApiAuth?.FirstOrDefault(item => item.Name == ApiName.Simkl)?.ClientId;

        if (string.IsNullOrEmpty(simklClientId)) {
            logger.LogWarning("({Provider}) No client ID is set; add one on the AniDoki settings page", ApiName.Simkl);
        }

        return simklClientId;
    }

    public static bool GetSimklUpdateAll() => Plugin.Instance?.PluginConfiguration.simklUpdateAll ?? false;

    /// <summary>
    /// Deauthenticate a user.
    /// </summary>
    /// <param name="userId">ID of the user you want to deauthenticate.</param>
    /// <param name="provider">Provider you want to deauthenticate from the user.</param>
    /// <returns>True if successful, false if unsuccessful and the reason why.</returns>
    public static (bool success, string? reason) DeauthenticateUser(Guid userId, ApiName provider) {
        if (Plugin.Instance == null)
            return (false, "Plugin instance null.");
        lock (Plugin.Instance.PluginConfiguration) {
            UserConfig? userConfig = Plugin.Instance.PluginConfiguration.UserConfig?.FirstOrDefault(userConfig => userConfig.UserId == userId);

            if (userConfig == null)
                return (false, "User configuration not found.");
            userConfig.UserApiAuth = (userConfig.UserApiAuth ?? []).Where(userApiAuth => userApiAuth.Name != provider).ToArray();
            Plugin.Instance.SaveConfiguration();
            return (true, null);
        }
    }

    /// <summary>Whether the admin turned on the user pages; their routes answer 404 while it is off.</summary>
    internal static bool UserPagesEnabled() {
        return Plugin.Instance?.PluginConfiguration.enableUserPages == true;
    }

    /// <summary>The saved app settings of an OAuth tracker, or null when the tracker does not sign in with OAuth or its settings are incomplete.</summary>
    internal static ProviderApiAuth? GetConfiguredOAuthProvider(ApiName provider) {
        if (provider is not (ApiName.Mal or ApiName.AniList or ApiName.Shikimori or ApiName.Simkl))
            return null;
        var configuration = Plugin.Instance?.PluginConfiguration;
        var auth = configuration?.ProviderApiAuth?.FirstOrDefault(auth => auth.Name == provider);
        if (string.IsNullOrWhiteSpace(auth?.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret) ||
            (provider == ApiName.Shikimori && string.IsNullOrWhiteSpace(configuration?.shikimoriAppName)))
            return null;
        return auth;
    }
}
