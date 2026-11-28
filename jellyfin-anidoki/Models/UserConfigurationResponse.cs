using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Models;

public sealed class UserConfigurationResponse {
    public UserConfigurationResponse(UserConfig? config) {
        PlanToWatchOnly = config?.PlanToWatchOnly ?? true;
        RewatchCompleted = config?.RewatchCompleted ?? true;
        AddUnlistedAnime = config?.AddUnlistedAnime ?? false;
        ShowLogNotifications = config?.ShowLogNotifications ?? true;
        TrackerThresholdPercent = config?.TrackerThresholdPercent;
        ConnectedProviders = config?.UserApiAuth?
            .Where(auth => !string.IsNullOrWhiteSpace(auth.AccessToken))
            .Select(auth => auth.Name).Distinct().ToArray() ?? Array.Empty<ApiName>();
    }

    public bool PlanToWatchOnly { get; }
    public bool RewatchCompleted { get; }
    public bool AddUnlistedAnime { get; }
    public bool ShowLogNotifications { get; }
    public int? TrackerThresholdPercent { get; }
    public ApiName[] ConnectedProviders { get; }
}
