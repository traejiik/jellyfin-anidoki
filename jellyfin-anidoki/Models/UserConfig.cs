using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

public class UserConfig : UserEditableConfig {
    public UserConfig() {
        // set default options here
        PlanToWatchOnly = true;
        RewatchCompleted = true;
    }

    /// <summary>
    /// ID of the user.
    /// </summary>
    public Guid UserId { get; set; }
    public bool DiscordNotificationsEnabled { get; set; }
    /// <summary>Stored in the AniDoki database; never written to the plugin XML or JSON.</summary>
    [XmlIgnore, JsonIgnore]
    public string? DiscordWebhookUrl { get; set; }
    public long DiscordNotificationRevision { get; set; }

    /// <summary>
    /// The libraries this user picked before 2.0.0 (null or empty: every library). Read from the saved XML only so the
    /// first start of 2.0.0 can seed the admin's anime libraries from it (D9), then cleared; never sent to the pages.
    /// </summary>
    [JsonIgnore]
    public string[]? LibraryToCheck { get; set; }

    /// <summary>
    /// API authentication details of the user.
    /// </summary>
    public UserApiAuth[]? UserApiAuth { get; set; }

    /// <summary>
    /// Key pair values for any data that needs to be stored but doesn't fit anywhere else.
    /// </summary>
    public List<KeyPairs>? KeyPairs { get; set; }

    public void AddUserApiAuth(UserApiAuth userApiAuth) {
        if (UserApiAuth != null) {
            var apiAuthList = UserApiAuth.ToList();
            apiAuthList.Add(userApiAuth);
            UserApiAuth = apiAuthList.ToArray();
        } else {
            UserApiAuth = new[] { userApiAuth };
        }
    }
}

public class UserEditableConfig {
    /// <summary>
    /// Gets or sets a value indicating whether the API should only search for shows on the users plan to watch list.
    /// </summary>
    public bool PlanToWatchOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should automatically set completed shows as re-watching.
    /// </summary>
    public bool RewatchCompleted { get; set; }

    /// <summary>
    /// With <see cref="PlanToWatchOnly"/> on, also add a title that is not on the user's list at all (D8). Off by default;
    /// with Plan to watch only off, unlisted titles are added anyway.
    /// </summary>
    public bool AddUnlistedAnime { get; set; }

    public bool ShowLogNotifications { get; set; } = true;

    /// <summary>
    /// The percentage at which playback updates the trackers while the video plays (60 to the server's
    /// <c>MaxResumePct</c>, in steps of 5); null updates them on player exit at Jellyfin's own percentage, the default.
    /// </summary>
    public int? TrackerThresholdPercent { get; set; }
}
