using System;
using System.Text.Json.Serialization;

namespace jellyfin_anidoki.Models.Shikimori;

public class ShikimoriUserRate {
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StatusEnum Status { get; set; }

    [JsonPropertyName("episodes")]
    public int Episodes { get; set; }

    [JsonPropertyName("rewatches")]
    public int Rewatches { get; set; }

    /// <summary>The score, 1–10; 0 when unrated.</summary>
    [JsonPropertyName("score")]
    public int? Score { get; set; }

    public enum StatusEnum {
        planned,
        watching,
        rewatching,
        completed,
        on_hold,
        dropped
    }
}
