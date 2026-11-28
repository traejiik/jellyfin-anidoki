using System;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace jellyfin_anidoki.Configuration;

public class UserApiAuth {
    /// <summary>
    /// Name of the API provider.
    /// </summary>
    public ApiName Name { get; set; }
    public Guid ConnectionGeneration { get; set; }
    /// <summary>
    /// Access token of the authenticated instance. Stored in the AniDoki database; never written to the plugin XML or JSON.
    /// </summary>
    [XmlIgnore, JsonIgnore]
    public string? AccessToken { get; set; }
    /// <summary>
    /// Refresh token of the authenticated instance. Stored in the AniDoki database; never written to the plugin XML or JSON.
    /// </summary>
    [XmlIgnore, JsonIgnore]
    public string? RefreshToken { get; set; }
}
