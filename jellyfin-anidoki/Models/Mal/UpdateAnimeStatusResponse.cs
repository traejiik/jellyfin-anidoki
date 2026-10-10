using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace jellyfin_anidoki.Models.Mal {
    public class UpdateAnimeStatusResponse : ListStatus {
        // JSON-ignored receipt capabilities for providers whose mutation returns a bool.
        [JsonIgnore] public bool UsesAcknowledgementFields { get; set; }
        [JsonIgnore] public int? AcknowledgedProgress { get; set; }
        [JsonIgnore] public Status? AcknowledgedStatus { get; set; }
        [JsonIgnore] public bool? AcknowledgedRewatching { get; set; }
        [JsonIgnore] public int? AcknowledgedRewatchCount { get; set; }

        [JsonPropertyName("finish_date")] public string FinishDate { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; }

        [JsonPropertyName("num_times_rewatched")]
        public int NumTimesRewatched { get; set; }

        [JsonPropertyName("rewatch_value")] public int RewatchValue { get; set; }
        [JsonPropertyName("tags")] public List<object> Tags { get; set; }
        [JsonPropertyName("comments")] public string Comments { get; set; }
    }
}