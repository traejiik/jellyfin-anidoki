using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

public sealed record AiringSchedule(
    [property: JsonPropertyName("episode")] int Episode,
    [property: JsonPropertyName("airsAtUtc")] DateTimeOffset AirsAtUtc);
public sealed record AiringView([property: JsonPropertyName("airing"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] AiringSchedule? Airing);
public sealed record RelatedLibraryItem(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("season")] int? Season);
public sealed record RelatedEntry(
    [property: JsonPropertyName("kind"), JsonConverter(typeof(JsonStringEnumConverter))] RelationKind Kind,
    [property: JsonPropertyName("entryId")] int EntryId,
    [property: JsonPropertyName("alternativeId")] string? AlternativeId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("libraryMatches")] IReadOnlyList<RelatedLibraryItem> LibraryMatches,
    [property: JsonPropertyName("coverUrl")] string? CoverUrl);
public sealed record RelatedView([property: JsonPropertyName("relations")] IReadOnlyList<RelatedEntry> Relations);

public sealed class SeriesDetails(ISeriesItems items, ProgressSources sources, SeriesProgressReader reader, IMatchTrackers trackers,
    AniListAiring airing, ILogger<SeriesDetails> logger) {
    internal LibrarySource? Select(Guid itemId, Guid userId, string? sourceKey) {
        if (items.Find(itemId, userId) is not { } item)
            return null;
        var eligible = sources.Sources(item, userId);
        return sourceKey == null ? sources.Default(item, eligible, userId)?.Source : eligible.FirstOrDefault(source => source.Info.SourceKey == sourceKey);
    }

    internal bool Linked(Guid userId, ApiName provider) => trackers.ForOwner(userId, provider) != null;

    internal async Task<RelatedView?> Related(LibrarySource source, Guid userId, ApiName provider, int? entryId, bool applySaved,
        CancellationToken cancellationToken) {
        if (trackers.ForOwner(userId, provider) is not { } tracker)
            return null;
        try {
            var match = await reader.ResolveMatch(source, userId, tracker, applySaved, cancellationToken);
            var entry = match.Match == null ? null : ProgressSources.MappedEntries(source, match.Match)
                .FirstOrDefault(entry => entryId == null || entry.Id == entryId);
            if (entry == null)
                return entryId == null ? new RelatedView([]) : null;

            var related = await tracker.GetRelations(entry, cancellationToken) ?? [];
            if (related.Count == 0)
                return new RelatedView([]);
            var hits = related.Select(_ => new List<RelatedLibraryItem>()).ToArray();
            var localTargets = sources.LocalTargets();
            foreach (var item in sources.Items(userId)) {
                foreach (var candidate in sources.Sources(item, userId)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var saved = sources.Saved(candidate, userId, provider, applySaved);
                    var unseen = Enumerable.Range(0, related.Count).ToHashSet();
                    foreach (int number in candidate.Episodes) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = saved == null ? (await localTargets(candidate, provider, number))?.Entry
                            : (RangeResolver.Map(saved, number) as MappedEpisode)?.Target.Entry;
                        if (target == null)
                            continue;
                        foreach (int index in unseen.Where(index => ProgressSources.SameEntry(target, related[index].Entry)).ToArray()) {
                            hits[index].Add(new RelatedLibraryItem(item.Id, SourceKeys.Parse(candidate.Info.SourceKey)?.SeasonNumber));
                            unseen.Remove(index);
                        }
                        if (unseen.Count == 0)
                            break;
                    }
                }
            }
            return new RelatedView(related.Select((relation, index) =>
                new RelatedEntry(relation.Kind, relation.Entry.Id, relation.Entry.AlternativeId, relation.Title, hits[index],
                    Uri.TryCreate(relation.CoverUrl, UriKind.Absolute, out Uri? cover) && cover.Scheme == Uri.UriSchemeHttps
                        ? cover.AbsoluteUri : null)).ToList());
        } catch (Exception error) when (ProgressSources.IsUpstreamFailure(error, cancellationToken)) {
            logger.LogDebug("({Provider}) Could not read related entries: {ErrorMessage}", provider, error.Message);
            return new RelatedView([]);
        }
    }

    internal async Task<AiringView?> Airing(LibrarySource source, Guid userId, int? entryId, bool applySaved, CancellationToken cancellationToken) {
        try {
            if (entryId != null) {
                if (trackers.ForOwner(userId, ApiName.AniList) is not { } tracker)
                    return null;
                var match = await reader.ResolveMatch(source, userId, tracker, applySaved, cancellationToken);
                if (match.Match == null || !ProgressSources.MappedEntries(source, match.Match).Any(entry => entry.Id == entryId))
                    return null;
            } else {
                var saved = sources.Saved(source, userId, ApiName.AniList, applySaved && Linked(userId, ApiName.AniList));
                entryId = saved == null ? (await sources.Known(source, ApiName.AniList, cachedOnly: false))?.Id
                    : ProgressSources.MappedEntries(source, saved).FirstOrDefault()?.Id;
            }
            return new AiringView(entryId is > 0 ? await airing.Read(entryId.Value, cancellationToken) : null);
        } catch (Exception error) when (ProgressSources.IsUpstreamFailure(error, cancellationToken)) {
            logger.LogDebug("({Provider}) Could not read the airing schedule: {ErrorMessage}", ApiName.AniList, error.Message);
            return new AiringView(null);
        }
    }
}

public sealed class AniListAiring(IHttpClientFactory clients, IMemoryCache cache, TimeProvider clock, ILogger<AniListAiring> logger) {
    private sealed record Cached(AiringSchedule? Schedule, DateTimeOffset Expires);
    private sealed record Response([property: JsonPropertyName("data")] JsonElement Data,
        [property: JsonPropertyName("errors")] JsonElement Errors);
    private sealed record Media(AiringData? NextAiringEpisode);
    private sealed record AiringData(int Episode, long AiringAt);

    internal async Task<AiringSchedule?> Read(int id, CancellationToken cancellationToken) {
        var key = (nameof(AniListAiring), id);
        var now = clock.GetUtcNow();
        if (cache.TryGetValue(key, out Cached? cached) && cached != null && cached.Expires > now
            && (cached.Schedule == null || cached.Schedule.AirsAtUtc > now))
            return cached.Schedule;

        cancellationToken.ThrowIfCancellationRequested();
        using var client = clients.CreateClient(NamedClient.Default);
        const string query = "query ($id: Int!) { Media(id: $id, type: ANIME) { nextAiringEpisode { episode airingAt } } }";
        var response = await GraphQlHelper.DeserializeRequest<Response>(client, logger, ApiName.AniList, cache, new Delayer(), query,
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);
        if (response?.Data.ValueKind != JsonValueKind.Object || response.Errors.ValueKind == JsonValueKind.Array && response.Errors.GetArrayLength() > 0
            || !response.Data.TryGetProperty("Media", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("nextAiringEpisode", out _))
            throw new JsonException("AniList did not return airing data");

        var data = value.Deserialize<Media>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?.NextAiringEpisode;
        now = clock.GetUtcNow();
        AiringSchedule? schedule = null;
        if (data != null) {
            if (data.Episode <= 0 || data.AiringAt is <= 0 or > 253402300799)
                throw new JsonException("AniList returned an invalid airing schedule");
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(data.AiringAt);
            if (timestamp <= now)
                return null;
            schedule = new AiringSchedule(data.Episode, timestamp);
        }
        cache.Set(key, new Cached(schedule, now.AddHours(1)), TimeSpan.FromHours(1));
        return schedule;
    }
}
