using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using jellyfin_anidoki.Logging;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.AnimeIds;

/// <summary>
/// Looks up an anime's IDs on other trackers through an ARM server: the admin's, else the public one. A failed or unreadable
/// lookup is null, never an empty set of IDs. A successful answer is kept for <see cref="CacheFor"/>, so the same lookup in a
/// preview, its apply and the syncs after it asks ARM once; a failed or empty answer is never kept.
/// </summary>
/// <param name="configuration">The plugin settings, read on every lookup so a changed ARM server applies to the next one.</param>
/// <param name="clock">What expiry is measured with; the system clock unless a test passes its own.</param>
/// <param name="maxEntries">How many answers the cache holds at most.</param>
public sealed class ArmClient(IHttpClientFactory httpClientFactory, ILogger<ArmClient> logger, Func<PluginConfiguration?> configuration,
    TimeProvider? clock = null, int maxEntries = 10_000) {
    /// <summary>How long a successful answer is reused.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string BaseUrl, Source Source, int Id), (OfflineDatabaseResponse Ids, DateTimeOffset Expires)> _cache = new();

    internal int CachedCount => _cache.Count;

    public async Task<OfflineDatabaseResponse?> GetIds(int metadataId, Source source, bool cachedOnly = false) {
        // See https://arm.haglund.dev/docs#tag/v2/operation/v2-getIds
        string baseUrl = "https://arm.haglund.dev"; // fallback value, the public API
        string? customArmServerBaseUrl = configuration()?.armServerBaseUrl;
        if (!string.IsNullOrEmpty(customArmServerBaseUrl)) {
            if (Uri.TryCreate(customArmServerBaseUrl, UriKind.Absolute, out Uri? baseUri) && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps)) {
                baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
            } else {
                logger.LogWarning("The ARM server URL {Url} is not a valid http or https address; using the public server", customArmServerBaseUrl);
            }
        }

        var key = (baseUrl, source, metadataId);
        if (_cache.TryGetValue(key, out var cached) && _clock.GetUtcNow() < cached.Expires) {
            logger.LogDebug("ARM IDs for {Source} ID {MetadataId} came from the cache", source, metadataId);
            return Copy(cached.Ids);
        }

        if (cachedOnly)
            return null;

        HttpClient httpClient = httpClientFactory.CreateClient(NamedClient.Default);
        HttpResponseMessage response;
        long started = Stopwatch.GetTimestamp();
        try {
            response = await httpClient.GetAsync($"{baseUrl}/api/v2/ids?source={source.ToString().ToLower()}&id={metadataId}");
        } catch (Exception e) {
            logger.LogWarning("The ARM lookup of {Source} ID {MetadataId} failed before a response arrived: {ErrorMessage}", source, metadataId, e.Message);
            return null;
        }
        string streamText;
        using (response)
        using (var streamReader = new StreamReader(await response.Content.ReadAsStreamAsync())) {
            streamText = await streamReader.ReadToEndAsync();
        }

        // A payload that deserialises is not necessarily a usable result. ARM
        // returns literal `null` for an unknown ID and a partially-populated
        // object when the offline database has the entry but not yet the
        // cross-references. Those are very different situations, so the raw
        // response is logged and each case is reported separately below.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("ARM answered {StatusCode} for {Source} ID {MetadataId} in {ElapsedMs} ms: {ResponseBody}", (int)response.StatusCode, source, metadataId, LogText.ElapsedMs(started), LogText.Truncate(streamText));
        if (!response.IsSuccessStatusCode) {
            logger.LogWarning("The ARM server returned {StatusCode} for {Source} ID {MetadataId}; treating it as no result", (int)response.StatusCode, source, metadataId);
            return null;
        }

        OfflineDatabaseResponse? deserializedResponse;
        try {
            deserializedResponse = JsonSerializer.Deserialize<OfflineDatabaseResponse>(streamText);
        } catch (JsonException e) {
            logger.LogWarning("The ARM answer for {Source} ID {MetadataId} could not be read: {ErrorMessage}", source, metadataId, e.Message);
            return null;
        }

        if (deserializedResponse == null)
            return null;

        logger.LogDebug("ARM IDs for {Source} ID {MetadataId}: AniDB {AniDbId}, AniList {AniListId}, MyAnimeList {MalId}, Kitsu {KitsuId}", source, metadataId, Fmt(deserializedResponse.AniDb), Fmt(deserializedResponse.Anilist), Fmt(deserializedResponse.MyAnimeList), Fmt(deserializedResponse.Kitsu));

        Store(key, deserializedResponse);
        return deserializedResponse;
    }

    /// <summary>Keeps a successful answer. A full cache first drops expired answers, then empties if it is still full.</summary>
    private void Store((string BaseUrl, Source Source, int Id) key, OfflineDatabaseResponse ids) {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_cache.Count >= maxEntries) {
            foreach (var entry in _cache) {
                if (entry.Value.Expires <= now)
                    _cache.TryRemove(entry.Key, out _);
            }
            if (_cache.Count >= maxEntries)
                _cache.Clear();
        }
        _cache[key] = (Copy(ids), now + CacheFor);
    }

    private static OfflineDatabaseResponse Copy(OfflineDatabaseResponse ids) =>
        new() { Anilist = ids.Anilist, AniDb = ids.AniDb, MyAnimeList = ids.MyAnimeList, Kitsu = ids.Kitsu };

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
