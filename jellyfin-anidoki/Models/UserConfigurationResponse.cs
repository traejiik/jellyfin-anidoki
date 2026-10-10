#nullable enable
using System;
using System.Linq;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

public sealed class UserConfigurationResponse
{
    public UserConfigurationResponse(UserConfig? config)
    {
        LibraryToCheck = config?.LibraryToCheck ?? Array.Empty<string>();
        PlanToWatchOnly = config?.PlanToWatchOnly ?? true;
        RewatchCompleted = config?.RewatchCompleted ?? true;
        ShowLogNotifications = config?.ShowLogNotifications ?? true;
        ConnectedProviders = config?.UserApiAuth?
            .Where(auth => !string.IsNullOrWhiteSpace(auth.AccessToken))
            .Select(auth => auth.Name).Distinct().ToArray() ?? Array.Empty<ApiName>();
    }

    public string[] LibraryToCheck { get; }
    public bool PlanToWatchOnly { get; }
    public bool RewatchCompleted { get; }
    public bool ShowLogNotifications { get; }
    public ApiName[] ConnectedProviders { get; }
}
