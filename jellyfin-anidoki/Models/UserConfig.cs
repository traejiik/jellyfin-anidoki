#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models {
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
        public string DiscordWebhookUrl { get; set; }
        public long DiscordNotificationRevision { get; set; }

        /// <summary>
        /// API authentication details of the user.
        /// </summary>
        public UserApiAuth[] UserApiAuth { get; set; }

        /// <summary>
        /// Key pair values for any data that needs to be stored but doesn't fit anywhere else.
        /// </summary>
        public List<KeyPairs> KeyPairs { get; set; }

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

        public bool ShowLogNotifications { get; set; } = true;

        /// <summary>
        /// Libraries to sync from; empty means all. Defaults to empty so a configuration saved
        /// before this field existed reads as "all libraries", as the settings page shows it.
        /// </summary>
        public string[] LibraryToCheck { get; set; } = Array.Empty<string>();
    }
}
