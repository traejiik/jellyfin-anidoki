using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

public sealed class ProgressSources(IMatchLibrary library, ISeriesItems items, ISeasonWatchState watchState,
    MatchStore matches, ArmClient arm, AnimeListSource animeList, TimeProvider clock, ILogger<ProgressSources> logger) {
    /// <summary>How long a source ARM could not place is left alone before it is asked again.</summary>
    internal static readonly TimeSpan MissFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(string SourceKey, ApiName Provider), DateTimeOffset> _misses = new();

    internal IEnumerable<SeriesItem> Items(Guid userId) => library.List(userId)
        .Select(source => SourceKeys.Parse(source.SourceKey)?.ItemId)
        .OfType<Guid>().Distinct().Select(id => items.Find(id, userId)).OfType<SeriesItem>();

    internal IReadOnlyList<LibrarySource> Sources(SeriesItem item, Guid userId) =>
        item.SourceKeys.Select(key => library.Find(key, userId)).OfType<LibrarySource>().ToList();

    internal (LibrarySource Source, IReadOnlyList<WatchedEpisode> Watched)? Default(SeriesItem item,
        IReadOnlyList<LibrarySource> sources, Guid userId) {
        var rows = sources.Select(source => (Source: source, Watched: watchState.Watched(source.Last, userId))).ToList();
        string? key = ChooseDefault(item.OpenedSeason, rows.Select(row => (row.Source.Info.SourceKey, row.Watched.Max(episode => episode.LastPlayed))));
        return key == null ? null : rows.First(row => row.Source.Info.SourceKey == key);
    }

    internal static string? ChooseDefault(int? openedSeason, IEnumerable<(string Key, DateTime? LastPlayed)> sourceRows) {
        var rows = sourceRows.ToList();
        if (openedSeason != null && rows.FirstOrDefault(row => SourceKeys.Parse(row.Key)?.SeasonNumber == openedSeason).Key is { } opened)
            return opened;
        return rows.OrderByDescending(row => row.LastPlayed).FirstOrDefault().Key;
    }

    internal Match? Saved(LibrarySource source, Guid userId, ApiName provider, bool applySaved) =>
        applySaved ? matches.GetEffective(userId, provider, source.Info.SourceKey, true) : null;

    /// <summary>Where an episode syncs to, from Jellyfin's IDs and the cached ID lookups; with <paramref name="cachedOnly"/> false, ARM is asked too.</summary>
    internal Func<LibrarySource, ApiName, int, Task<(EntryRef Entry, int Episode)?>> LocalTargets(bool cachedOnly = true) {
        var localList = new Lazy<Task<AnimeListHelpers.AnimeListXml?>>(() => animeList.Read(cachedOnly: true));
        return (source, provider, episode) => {
            SyncSource? actual = source.VisibleEpisodes?.GetValueOrDefault(episode) is { } item ? SyncSource.From(item) : null;
            var focus = (actual ?? source.Last) with { EpisodeNumber = episode, IdsOnly = source.Last.IdsOnly };
            return KnownTarget(source with { Last = focus }, provider, cachedOnly, localList);
        };
    }

    /// <summary>
    /// Asks ARM about the sources whose first episode the cached lookups cannot place, four at a time, so a read that then uses
    /// only the caches finds them. ARM keeps an answer in memory for an hour and nothing across a restart. A source ARM cannot
    /// place is not asked about again for <see cref="MissFor"/> unless <paramref name="retryMisses"/>.
    /// </summary>
    internal async Task WarmUp(IEnumerable<LibrarySource> sources, ApiName provider, bool retryMisses, CancellationToken cancellationToken) {
        var cached = LocalTargets();
        var now = clock.GetUtcNow();
        var unknown = new List<LibrarySource>();
        foreach (var source in sources) {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Episodes.Count == 0 || !retryMisses && _misses.TryGetValue((source.Info.SourceKey, provider), out var until) && until > now)
                continue;
            if (await cached(source, provider, source.Episodes[0]) == null)
                unknown.Add(source);
        }
        if (unknown.Count == 0)
            return;

        long started = Stopwatch.GetTimestamp();
        var lookUp = LocalTargets(cachedOnly: false);
        await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (source, token) => {
                var key = (source.Info.SourceKey, provider);
                if (await lookUp(source, provider, source.Episodes[0]) == null)
                    _misses[key] = clock.GetUtcNow() + MissFor;
                else
                    _misses.TryRemove(key, out _);
            });
        logger.LogDebug("({Provider}) Looked up {Count} titles the ID cache did not have in {ElapsedMs} ms", provider, unknown.Count,
            LogText.ElapsedMs(started));
    }

    internal async Task<EntryRef?> Known(LibrarySource source, ApiName provider, bool cachedOnly) =>
        (await KnownTarget(source, provider, cachedOnly))?.Entry;

    private async Task<(EntryRef Entry, int Episode)?> KnownTarget(LibrarySource source, ApiName provider, bool cachedOnly,
        Lazy<Task<AnimeListHelpers.AnimeListXml?>>? localList = null) {
        SyncSource last = source.Last;
        if (last.Kind == MediaKind.Series && last.SeasonNumber == 0)
            return null;
        string? key = provider switch {
            ApiName.AniList => "AniList",
            ApiName.Mal => "MyAnimeList",
            ApiName.Kitsu => "Kitsu",
            _ => null
        };
        if (key == null)
            return null;
        int? direct = last.Item is Episode episode
            ? ReadId(episode.Season?.ProviderIds) ?? (last.SeasonNumber == 1 ? ReadId(episode.Series?.ProviderIds) : null)
            : ReadId(last.Item.ProviderIds);
        if (direct is { } directId)
            return (new EntryRef(provider, directId, null), last.Kind == MediaKind.Movie ? SyncSource.MovieProgress : last.EpisodeNumber);

        SourceIds resolved = await new SourceIdResolver(arm, animeList, logger).Resolve(last, cachedOnly, localList);
        int? id = provider switch {
            ApiName.AniList => resolved.Ids.Anilist ?? last.FallbackAniListId,
            ApiName.Mal => resolved.Ids.MyAnimeList,
            _ => resolved.Ids.Kitsu
        };
        return id is > 0 ? (new EntryRef(provider, id.Value, null), resolved.IdProgress(last)) : null;

        int? ReadId(Dictionary<string, string>? ids) =>
            AnimeListHelpers.TryGetProviderId(ids, key, out string? value) && int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
    }

    internal static IEnumerable<EntryRef> MappedEntries(LibrarySource source, Match match) =>
        source.Episodes.Select(number => RangeResolver.Map(match, number)).OfType<MappedEpisode>()
            .Select(mapped => mapped.Target.Entry).Distinct();

    internal static bool IsUpstreamFailure(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error is TrackerFailureException or HttpRequestException or IOException or JsonException or TaskCanceledException;

    internal static bool SameEntry(EntryRef left, EntryRef right) =>
        left.Provider == right.Provider && (left.Id > 0 && left.Id == right.Id
            || left.AlternativeId != null && left.AlternativeId == right.AlternativeId);
}
