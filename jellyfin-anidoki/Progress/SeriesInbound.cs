using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

/// <summary>One numbered episode of a season (or a movie, as episode 1) and the user's played state of it.</summary>
public sealed record LibraryEpisode(int Number, BaseItem Item, bool Played, bool Ignored);

/// <summary>A season's episodes and how to mark them played. The boundary between Sync to Jellyfin and Jellyfin's user data.</summary>
public interface IPlayedEpisodes {
    IReadOnlyList<LibraryEpisode> Episodes(LibrarySource source, Guid userId);

    /// <summary>Marks the items played now; a season is marked too when <paramref name="marksSeason"/> is true. Never unplays.</summary>
    void Mark(LibrarySource source, Guid userId, IReadOnlyList<BaseItem> items, bool marksSeason);
}

public sealed class JellyfinPlayedEpisodes(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogger<JellyfinPlayedEpisodes> logger)
    : IPlayedEpisodes {
    public IReadOnlyList<LibraryEpisode> Episodes(LibrarySource source, Guid userId) {
        if (users.GetUserById(userId) is not { } user || SourceKeys.Parse(source.Info.SourceKey) is not var (itemId, seasonNumber))
            return [];
        BaseItem? item = library.GetItemById(itemId);
        if (seasonNumber == null)
            return item is Movie movie ? [Entry(user, SyncSource.MovieProgress, movie)] : [];
        if (Season(item, seasonNumber.Value) is not { } season)
            return [];
        return season.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>()
            .Where(episode => episode.IndexNumber != null)
            .Select(episode => Entry(user, episode.IndexNumber!.Value, episode))
            .ToList();
    }

    public void Mark(LibrarySource source, Guid userId, IReadOnlyList<BaseItem> items, bool marksSeason) {
        if (users.GetUserById(userId) is not { } user || SourceKeys.Parse(source.Info.SourceKey) is not var (itemId, seasonNumber))
            return;
        var marker = new PlayedMarker(userData, logger);
        BaseItem? root = library.GetItemById(itemId);
        if (seasonNumber is { } number && Season(root, number) is { } season)
            marker.Mark(user, season, items.OfType<Episode>().ToList(), DateTime.UtcNow, marksSeason);
        else if (root is Movie movie && items.Count > 0)
            marker.MarkItem(user, movie, DateTime.UtcNow);
    }

    private static Season? Season(BaseItem? item, int number) =>
        (item as Series)?.Children.OfType<Season>().FirstOrDefault(season => season.IndexNumber == number);

    private LibraryEpisode Entry(User user, int number, BaseItem item) =>
        new(number, item, userData.GetUserData(user, item) is { Played: true }, SyncHelper.MediaShouldBeIgnored(item));
}

/// <summary>
/// Sync to Jellyfin for one series and one tracker: each entry's progress is mapped back through the same match the
/// popup shows, and the season's unplayed episodes up to it are marked played. Already played episodes keep their
/// played date, and nothing is ever unplayed.
/// </summary>
public sealed class SeriesInbound(SeriesProgressReader reader, IPlayedEpisodes episodes, ILogger<SeriesInbound> logger) {
    public const string Step = "mark-played";

    internal async Task Run(SeriesItem item, Guid userId, ITrackerProvider tracker, bool applySavedMatches, OperationContext operation) {
        IReadOnlyList<LibrarySource> sources = reader.Sources(item, userId);
        operation.ReportProgress(0, sources.Count);
        for (int i = 0; i < sources.Count; i++) {
            operation.CancellationToken.ThrowIfCancellationRequested();
            LibrarySource source = sources[i];
            string title = SeasonTitle(item, source);
            try {
                SeasonMatch season = await reader.Resolve(source, userId, tracker, applySavedMatches, operation.CancellationToken);
                if (season.Match == null) {
                    logger.LogInformation("({Provider}) Could not find an entry for {SeriesName}: {ErrorMessage}", tracker.Name, title, season.Unresolved);
                    operation.Record(Outcome(tracker.Name, source, title, OutcomeKind.Unresolved, "resolve", null));
                    continue;
                }
                IReadOnlyList<LibraryEpisode> library = episodes.Episodes(source, userId);
                var (toMark, marksSeason) = Plan(season, library);
                if (toMark.Count == 0) {
                    logger.LogInformation("({Provider}) {SeriesName} is already played as far as the tracker says", tracker.Name, title);
                    operation.Record(Outcome(tracker.Name, source, title, OutcomeKind.NoChange, Step, null));
                    continue;
                }
                episodes.Mark(source, userId, toMark.Select(episode => episode.Item).ToList(), marksSeason);
                logger.LogInformation("({Provider}) Marked {Count} episodes of {SeriesName} played", tracker.Name, toMark.Count, title);
                operation.Record(Outcome(tracker.Name, source, title, OutcomeKind.Confirmed, Step, toMark.Max(episode => episode.Number)));
            } catch (TrackerFailureException e) {
                var (kind, step) = Execution.Executor.FailureKindOutcome(e.Kind);
                logger.LogWarning("({Provider}) Could not read {SeriesName} to sync it to Jellyfin: {ErrorMessage}", tracker.Name, title, e.Message);
                operation.Record(Outcome(tracker.Name, source, title, kind, step, null));
            }
            operation.ReportProgress(i + 1, sources.Count);
        }
    }

    /// <summary>
    /// The unplayed, unignored episodes whose mapped entry episode the tracker has reached, and whether the season
    /// itself can be marked: every episode would then be played and none is ignored.
    /// </summary>
    internal static (IReadOnlyList<LibraryEpisode> ToMark, bool MarksSeason) Plan(SeasonMatch season, IReadOnlyList<LibraryEpisode> library) {
        var toMark = new List<LibraryEpisode>();
        foreach (LibraryEpisode episode in library) {
            if (episode.Played || episode.Ignored || season.Match is not { } match || RangeResolver.Map(match, episode.Number) is not MappedEpisode mapped)
                continue;
            if (season.Entries.GetValueOrDefault(mapped.Target.Entry) is { } state && mapped.TargetEpisode <= Reached(state))
                toMark.Add(episode);
        }
        bool marksSeason = library.Count > 0 && library.All(episode => !episode.Ignored && (episode.Played || toMark.Contains(episode)));
        return (toMark, marksSeason);
    }

    /// <summary>How far the tracker says the user is in an entry: its progress, or all of it once Completed.</summary>
    internal static int Reached(EntryState state) => state.List switch {
        null => 0,
        { Status: TrackerStatus.Completed } => state.TotalEpisodes > 0 ? Math.Max(state.TotalEpisodes, state.List.Progress) : int.MaxValue,
        { } list => list.Progress
    };

    private static string SeasonTitle(SeriesItem item, LibrarySource source) =>
        SourceKeys.Parse(source.Info.SourceKey)?.SeasonNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"{item.Name} season {number}")
            : item.Name;

    private static ProviderWriteOutcome Outcome(ApiName provider, LibrarySource source, string title, OutcomeKind kind, string step, int? progress) =>
        new(provider, source.Info.SourceKey, title, kind, progress, null, false, step, kind == OutcomeKind.Confirmed);
}
