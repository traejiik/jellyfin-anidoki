using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LibraryMatchState { OnList, NotOnList, Unresolved }

public sealed record LibraryProvider(
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider,
    [property: JsonPropertyName("available")] bool Available);
public sealed record LibraryProgressRow(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isMovie")] bool IsMovie,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("hasPoster")] bool HasPoster,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("episodeCount")] int EpisodeCount,
    [property: JsonPropertyName("playedCount")] int PlayedCount,
    [property: JsonPropertyName("matchState")] LibraryMatchState MatchState,
    [property: JsonPropertyName("entryId")] int? EntryId,
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))] TrackerStatus? Status,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("unloggedLibraryCount")] int? UnloggedLibraryCount,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("totalEpisodes")] int? TotalEpisodes,
    [property: JsonPropertyName("isAiring")] bool? IsAiring,
    [property: JsonPropertyName("nextAiringAtUtc")] DateTimeOffset? NextAiringAtUtc);
public sealed record LibraryStatusCount(
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))] TrackerStatus Status,
    [property: JsonPropertyName("count")] int Count);
public sealed record LibraryProgressView(
    [property: JsonPropertyName("providers")] IReadOnlyList<LibraryProvider> Providers,
    [property: JsonPropertyName("selectedProvider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName? SelectedProvider,
    [property: JsonPropertyName("rows")] IReadOnlyList<LibraryProgressRow> Rows,
    [property: JsonPropertyName("statusCounts")] IReadOnlyList<LibraryStatusCount> StatusCounts,
    [property: JsonPropertyName("notOnListCount")] int NotOnListCount);

public sealed class LibraryProgress(ProgressSources sources, IMatchTrackers trackers, TrackerLibraryCache lists,
    TimeProvider clock, ILogger<LibraryProgress> logger) {
    internal async Task<LibraryProgressView?> Read(Guid userId, ApiName? requested, bool refresh, bool applySaved, CancellationToken cancellationToken,
        bool afterChange = false) {
        var links = trackers.Links(userId);
        var providers = links.Keys.Order().Select(provider => new LibraryProvider(provider, Supports(provider))).ToList();
        ApiName? selected = requested ?? providers.FirstOrDefault(provider => provider.Available)?.Provider;
        if (selected is not { } provider || !Supports(provider) || !links.TryGetValue(provider, out var generation))
            return new LibraryProgressView(providers, null, [], [], 0);

        if (trackers.ForOwner(userId, provider) is not ITrackerLibraryList tracker)
            return null;
        IReadOnlyList<TrackerLibraryEntry>? entries;
        try {
            entries = await lists.Read(userId, provider, generation, tracker, refresh, cancellationToken, afterChange);
        } catch (Exception error) when (ProgressSources.IsUpstreamFailure(error, cancellationToken)) {
            logger.LogDebug("({Provider}) Could not read the library list: {ErrorMessage}", provider, error.Message);
            return null;
        }
        if (entries == null || !trackers.Links(userId).TryGetValue(provider, out var currentGeneration) || currentGeneration != generation)
            return null;
        // An account may have one entry in several custom lists. They have the same media ID.
        var byId = entries.ToLookup(entry => entry.Entry.Id);
        var rows = new List<LibraryProgressRow>();
        var titles = new List<(SeriesItem Item, LibrarySource Source, IReadOnlyList<WatchedEpisode> Watched, Match? Saved)>();
        foreach (var item in sources.Items(userId)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Default(item, sources.Sources(item, userId), userId) is var (source, watched))
                titles.Add((item, source, watched, sources.Saved(source, userId, provider, applySaved)));
        }
        // ARM's answers do not outlive a restart or an hour; without them most automatic matches would read as unresolved.
        await sources.WarmUp(titles.Where(title => title.Saved == null).Select(title => title.Source), provider, refresh, cancellationToken);

        var localTargets = sources.LocalTargets();
        var now = clock.GetUtcNow();
        foreach (var (item, source, watched, saved) in titles) {
            cancellationToken.ThrowIfCancellationRequested();
            EntryRef? target = null;
            var mappedEpisodes = new List<(int Source, int Target)>();
            if (saved == null) {
                int first = source.Episodes[0];
                if (await localTargets(source, provider, first) is { } firstTarget) {
                    target = firstTarget.Entry;
                    mappedEpisodes.Add((first, firstTarget.Episode));
                    foreach (int episode in source.Episodes.Skip(1)) {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (await localTargets(source, provider, episode) is { } mapped && ProgressSources.SameEntry(target, mapped.Entry))
                            mappedEpisodes.Add((episode, mapped.Episode));
                    }
                }
            } else {
                foreach (int episode in source.Episodes) {
                    if (RangeResolver.Map(saved, episode) is not MappedEpisode mapped)
                        continue;
                    target ??= mapped.Target.Entry;
                    if (ProgressSources.SameEntry(target, mapped.Target.Entry))
                        mappedEpisodes.Add((episode, mapped.TargetEpisode));
                }
            }
            var episodeNumbers = target == null ? source.Episodes : mappedEpisodes.Select(episode => episode.Source).ToList();
            var entry = target == null ? null : byId[target.Id].FirstOrDefault();
            LibraryMatchState state = target == null ? LibraryMatchState.Unresolved
                : entry == null ? LibraryMatchState.NotOnList : LibraryMatchState.OnList;
            int? unlogged = entry == null ? null : item.IsMovie ? Math.Clamp(1 - entry.List.Progress, 0, 1)
                : mappedEpisodes.Count(episode => episode.Target > entry.List.Progress);
            rows.Add(new LibraryProgressRow(item.Id, item.Name, item.IsMovie, item.Year, item.Images.Contains("Primary"),
                source.Info.SourceKey, episodeNumbers.Count, watched.Select(episode => episode.Number).Distinct().Count(episodeNumbers.Contains),
                state, target?.Id, entry?.List.Status, entry?.List.Progress, unlogged, entry?.List.Score,
                item.IsMovie ? 1 : entry?.TotalEpisodes, entry?.IsAiring, entry?.NextAiringAtUtc > now ? entry.NextAiringAtUtc : null));
        }

        var statusCounts = rows.Where(row => row.Status != null).ToLookup(row => row.Status);
        return new LibraryProgressView(providers, provider, rows.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.ItemId).ToList(),
            Enum.GetValues<TrackerStatus>().Select(status => new LibraryStatusCount(status, statusCounts[status].Count())).ToList(),
            rows.Count(row => row.MatchState == LibraryMatchState.NotOnList));
    }

    internal static bool Supports(ApiName provider) => provider is ApiName.Mal or ApiName.AniList or ApiName.Kitsu;
}

public sealed class TrackerLibraryCache(IMemoryCache cache, TimeProvider clock) {
    private readonly record struct Key(Guid UserId, ApiName Provider, Guid Generation);
    private sealed record Snapshot(IReadOnlyList<TrackerLibraryEntry> Entries, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<Key, Lazy<Task<IReadOnlyList<TrackerLibraryEntry>?>>> _pending = new();

    internal Task<IReadOnlyList<TrackerLibraryEntry>?> Read(Guid userId, ApiName provider, Guid generation, ITrackerLibraryList tracker,
        bool refresh, CancellationToken cancellationToken, bool afterChange = false) {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new Key(userId, provider, generation);
        if (_pending.TryGetValue(key, out var pending))
            return afterChange ? RefreshAfter(pending.Value) : pending.Value.WaitAsync(cancellationToken);
        refresh |= afterChange;
        if (!refresh && cache.TryGetValue(key, out Snapshot? snapshot) && snapshot != null && snapshot.Expires > clock.GetUtcNow())
            return Task.FromResult<IReadOnlyList<TrackerLibraryEntry>?>(snapshot.Entries);

        return _pending.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<TrackerLibraryEntry>?>>(Load)).Value.WaitAsync(cancellationToken);

        async Task<IReadOnlyList<TrackerLibraryEntry>?> RefreshAfter(Task<IReadOnlyList<TrackerLibraryEntry>?> previous) {
            // A refresh started before the write may have fetched an older list.
            try {
                await previous.WaitAsync(cancellationToken);
            } catch (Exception error) when (ProgressSources.IsUpstreamFailure(error, cancellationToken)) {
                // A failed earlier read still allows this explicit refresh.
            }
            return await Read(userId, provider, generation, tracker, true, cancellationToken);
        }

        async Task<IReadOnlyList<TrackerLibraryEntry>?> Load() {
            try {
                // One browser navigating away must not cancel a refresh other requests are waiting for.
                if (!refresh && cache.TryGetValue(key, out Snapshot? current) && current != null && current.Expires > clock.GetUtcNow())
                    return current.Entries;
                var entries = await tracker.ReadLibraryList(CancellationToken.None);
                if (entries != null)
                    cache.Set(key, new Snapshot(entries, clock.GetUtcNow().AddMinutes(10)), TimeSpan.FromMinutes(10));
                return entries;
            } finally {
                _pending.TryRemove(key, out _);
            }
        }
    }
}
