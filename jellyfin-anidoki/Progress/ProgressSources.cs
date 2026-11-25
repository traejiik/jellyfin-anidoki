using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

public sealed class ProgressSources(IMatchLibrary library, ISeriesItems items, ISeasonWatchState watchState,
    MatchStore matches, ArmClient arm, AnimeListSource animeList, ILogger<ProgressSources> logger) {
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

    internal Func<LibrarySource, ApiName, int, Task<EntryRef?>> LocalIds() {
        var localList = new Lazy<Task<AnimeListHelpers.AnimeListXml?>>(() => animeList.Read(cachedOnly: true));
        return (source, provider, episode) => {
            SyncSource? actual = source.VisibleEpisodes?.GetValueOrDefault(episode) is { } item ? SyncSource.From(item) : null;
            var focus = (actual ?? source.Last) with { EpisodeNumber = episode, IdsOnly = source.Last.IdsOnly };
            return Known(source with { Last = focus }, provider, cachedOnly: true, localList);
        };
    }

    internal async Task<EntryRef?> Known(LibrarySource source, ApiName provider, bool cachedOnly,
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
            return new EntryRef(provider, directId, null);

        SourceIds resolved = await new SourceIdResolver(arm, animeList, logger).Resolve(last, cachedOnly, localList);
        int? id = provider switch {
            ApiName.AniList => resolved.Ids.Anilist ?? last.FallbackAniListId,
            ApiName.Mal => resolved.Ids.MyAnimeList,
            _ => resolved.Ids.Kitsu
        };
        return id is > 0 ? new EntryRef(provider, id.Value, null) : null;

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
