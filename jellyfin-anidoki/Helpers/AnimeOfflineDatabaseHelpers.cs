#nullable disable
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers {
    public class AnimeOfflineDatabaseHelpers {
        public static async Task<OfflineDatabaseResponse> GetProviderIdsFromMetadataProvider(HttpClient httpClient, ILogger logger, int metadataId, Source source) {
            // See https://arm.haglund.dev/docs#tag/v2/operation/v2-getIds
            string baseUrl = "https://arm.haglund.dev"; // fallback value, the public API
            string customArmServerBaseUrl = Plugin.Instance?.Configuration.armServerBaseUrl;
            if (!string.IsNullOrEmpty(customArmServerBaseUrl)) {
                if (Uri.TryCreate(customArmServerBaseUrl, UriKind.Absolute, out Uri baseUri) && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps)) {
                    baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
                } else {
                    logger.LogWarning("The ARM server URL {Url} is not a valid http or https address; using the public server", customArmServerBaseUrl);
                }
            }

            HttpResponseMessage response;
            try {
                response = await httpClient.GetAsync($"{baseUrl}/api/v2/ids?source={source.ToString().ToLower()}&id={metadataId}");
            } catch (Exception e) {
                logger.LogWarning("The ARM lookup of {Source} ID {MetadataId} failed before a response arrived: {ErrorMessage}", source, metadataId, e.Message);
                return null;
            }
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            string streamText = await streamReader.ReadToEndAsync();

            // A payload that deserialises is not necessarily a usable result. ARM
            // returns literal `null` for an unknown ID and a partially-populated
            // object when the offline database has the entry but not yet the
            // cross-references. Those are very different situations, so the raw
            // response is logged and each case is reported separately below.
            logger.LogDebug("ARM answered {StatusCode} for {Source} ID {MetadataId}: {ResponseBody}", (int)response.StatusCode, source, metadataId, LogText.Truncate(streamText));
            if (!response.IsSuccessStatusCode) {
                logger.LogWarning("The ARM server returned {StatusCode} for {Source} ID {MetadataId}; treating it as no result", (int)response.StatusCode, source, metadataId);
                return null;
            }

            OfflineDatabaseResponse deserializedResponse;
            try {
                deserializedResponse = JsonSerializer.Deserialize<OfflineDatabaseResponse>(streamText);
            } catch (JsonException e) {
                logger.LogWarning("The ARM answer for {Source} ID {MetadataId} could not be read: {ErrorMessage}", source, metadataId, e.Message);
                return null;
            }

            if (deserializedResponse == null)
                return null;

            logger.LogDebug("ARM IDs for {Source} ID {MetadataId}: AniDB {AniDbId}, AniList {AniListId}, MyAnimeList {MalId}, Kitsu {KitsuId}", source, metadataId, Fmt(deserializedResponse.AniDb), Fmt(deserializedResponse.Anilist), Fmt(deserializedResponse.MyAnimeList), Fmt(deserializedResponse.Kitsu));

            return deserializedResponse;
        }

        private static string Fmt(int? value) => value?.ToString() ?? "<none>";

        public class OfflineDatabaseResponse {
            [JsonPropertyName("anilist")] public int? Anilist { get; set; }
            [JsonPropertyName("anidb")] public int? AniDb { get; set; }
            [JsonPropertyName("myanimelist")] public int? MyAnimeList { get; set; }
            [JsonPropertyName("kitsu")] public int? Kitsu { get; set; }
        }

        public enum Source {
            Anidb,
            Anilist,
            Myanimelist,
            Kitsu
        }

        public static Source MapFromApiName(ApiName apiName) {
            switch (apiName) {
                case ApiName.Mal:
                    return Source.Myanimelist;
                case ApiName.AniList:
                    return Source.Anilist;
                case ApiName.Kitsu:
                    return Source.Kitsu;
                default:
                    throw new ArgumentOutOfRangeException(nameof(apiName), apiName, null);
            }
        }
    }
}
