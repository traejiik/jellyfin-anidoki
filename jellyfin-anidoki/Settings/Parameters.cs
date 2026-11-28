using System.Collections.Generic;
using System.Dynamic;

namespace jellyfin_anidoki.Settings;

public class Parameters {
    public string? localIpAddress { get; set; }
    public int localPort { get; set; }
    public bool https { get; set; }
    public List<ExpandoObject>? providerList { get; set; }
    public List<ExpandoObject>? libraries { get; set; }
    /// <summary>The server's played percentage (Jellyfin's <c>MaxResumePct</c>), which caps a user's completion percentage.</summary>
    public int maxResumePct { get; set; }

    /// <summary>The admin's default completion percentage as it applies on this server, or null for player exit.</summary>
    public int? defaultThresholdPercent { get; set; }
}
