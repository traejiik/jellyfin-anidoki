#nullable disable
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace jellyfin_anidoki.Configuration {
    public class ProviderApiAuth {
        public ApiName Name { get; set; }
        public string ClientId { get; set; }
        /// <summary>
        /// Stored in the AniDoki database; never written to the plugin XML or JSON. The admin page sets it through
        /// <c>POST AniDoki/providerSecret</c>.
        /// </summary>
        [XmlIgnore, JsonIgnore]
        public string ClientSecret { get; set; }
    }
}
