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
                    logger.LogWarning("ARM server base URL ({CustomArmServerBaseUrl}) could not be parsed. Confirm the URL is valid. Falling back to public API...", customArmServerBaseUrl);
                }
            }

            HttpResponseMessage response;
            try {
                response = await httpClient.GetAsync($"{baseUrl}/api/v2/ids?source={source.ToString().ToLower()}&id={metadataId}");
            } catch (Exception e) {
                logger.LogError("Error encountered while retrieving provider IDs: {Message}\n{StackTrace}", e.Message, e.StackTrace);
                return null;
            }
            StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            string streamText = await streamReader.ReadToEndAsync();

            // A payload that deserialises is not necessarily a usable result. ARM
            // returns literal `null` for an unknown ID and a partially-populated
            // object when the offline database has the entry but not yet the
            // cross-references. Those are very different situations, so the raw
            // response is logged and each case is reported separately below.
            logger.LogDebug("[anidoki-diag] ARM {StatusCode} for {Source}={MetadataId}, body: {Body}", (int)response.StatusCode, source.ToString().ToLower(), metadataId, (streamText.Length > 400 ? streamText.Substring(0, 400) + "..." : streamText));
            if (!response.IsSuccessStatusCode) {
                logger.LogWarning("ARM server returned {StatusCode}; treating as no result", (int)response.StatusCode);
                return null;
            }

            OfflineDatabaseResponse deserializedResponse;
            try {
                deserializedResponse = JsonSerializer.Deserialize<OfflineDatabaseResponse>(streamText);
            } catch (JsonException e) {
                logger.LogError("Could not parse ARM server response: {ErrorMessage}", e.Message);
                return null;
            }

            if (deserializedResponse == null)
                return null;

            logger.LogDebug("[anidoki-diag] ARM parsed -> anidb={AniDb}, anilist={AniList}, myanimelist={MyAnimeList}, kitsu={Kitsu}", Fmt(deserializedResponse.AniDb), Fmt(deserializedResponse.Anilist), Fmt(deserializedResponse.MyAnimeList), Fmt(deserializedResponse.Kitsu));

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
