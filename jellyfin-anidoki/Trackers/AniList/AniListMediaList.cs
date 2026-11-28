using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Trackers.Mal;

namespace jellyfin_anidoki.Trackers.AniList;

public class AniListMediaList {
    public class Data {
        [JsonPropertyName("MediaListCollection")] public MediaListCollection? MediaListCollection { get; set; }
    }

    public class Entries {
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public AniListSearch.MediaListStatus? ListStatus { get; set; }
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("repeat")] public int? Repeat { get; set; }
        [JsonPropertyName("media")] public AniListSearch.Media? Media { get; set; }
        [JsonPropertyName("completedAt")] public AniListSearch.FuzzyDate? CompletedAt { get; set; }
        [JsonPropertyName("progress")] public int? Progress { get; set; }
    }

    public class EntriesContainer {
        [JsonPropertyName("entries")] public List<Entries>? Entries { get; set; }
    }

    public class MediaListCollection {
        [JsonPropertyName("lists")] public List<EntriesContainer>? MediaList { get; set; }

        [JsonPropertyName("hasNextChunk")] public bool? HasNextChunk { get; set; }
    }

    public class AniListUserMediaList {
        [JsonPropertyName("data")] public Data? Data { get; set; }
        [JsonPropertyName("errors")] public List<JsonElement>? Errors { get; set; }
    }
}
