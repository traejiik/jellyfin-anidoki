using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Progress;

/// <summary>One season's match for one tracker, with the entries it points at as the user's list holds them.</summary>
/// <param name="Match">Null when automatic resolution found nothing.</param>
/// <param name="Unresolved">Why <paramref name="Match"/> is null.</param>
internal sealed record SeasonMatch(LibrarySource Source, Match? Match, MatchOrigin Origin, string? Unresolved,
    IReadOnlyDictionary<EntryRef, EntryState> Entries);

/// <summary>
/// Builds the series view: each season the user syncs, each linked tracker's match for it (saved, else automatic), and
/// that tracker's entries with how far Jellyfin says the user is. Reads only; nothing is written.
/// </summary>
public sealed class SeriesProgressReader(ISeriesItems items, IMatchLibrary library, MatchStore matches, IMatchTrackers trackers,
    IAutomaticMatches automatic, ISeasonWatchState watchState, ScoreFormatCache formats, ILogger<SeriesProgressReader> logger) {
    /// <returns>Null when the item is not one the user can see, or none of its seasons syncs for them.</returns>
    public async Task<SeriesViewContract?> Read(Guid itemId, Guid userId, bool applySavedMatches, bool matchesLinkAvailable, CancellationToken cancellationToken) {
        if (items.Find(itemId, userId) is not { } item || Sources(item, userId) is not { Count: > 0 } sources)
            return null;

        IReadOnlyDictionary<ApiName, Guid> links = trackers.Links(userId);
        var watchedBySeason = sources.Select(source => watchState.Watched(source.Last, userId)).ToArray();
        var reads = new List<Task<(TrackerAccountContract Account, List<SeasonTrackerContract> Rows)>>();
        foreach (var (provider, generation) in links.OrderBy(link => link.Key)) {
            if (trackers.ForOwner(userId, provider) is { } tracker)
                reads.Add(ReadTracker(tracker, generation));
        }

        // Overlap independent trackers while keeping one request at a time per tracker.
        var results = await Task.WhenAll(reads);
        var seasons = new List<SeasonViewContract>();
        for (int index = 0; index < sources.Count; index++) {
            LibrarySource source = sources[index];
            IReadOnlyList<WatchedEpisode> watched = watchedBySeason[index];
            seasons.Add(new SeasonViewContract(source.Info.SourceKey, SourceKeys.Parse(source.Info.SourceKey)?.SeasonNumber, source.Episodes,
                watched.Select(episode => episode.Number).Distinct().Order().ToList(), watched.Max(episode => episode.LastPlayed),
                results.Select(result => result.Rows[index]).ToList()));
        }

        return new SeriesViewContract(item.Id, item.Name, item.IsMovie, item.Year, item.EndYear, item.CommunityRating, item.OfficialRating, item.Status,
            item.Overview, item.PremiereDate, item.Images, sources.Sum(source => source.Episodes.Count), DefaultSeason(item, seasons),
            matchesLinkAvailable, results.Select(result => result.Account).ToList(), seasons);

        async Task<(TrackerAccountContract, List<SeasonTrackerContract>)> ReadTracker(ITrackerProvider tracker, Guid generation) {
            var account = new TrackerAccountContract(tracker.Name, tracker.Capabilities.SupportsScore, await Format(userId, generation, tracker, cancellationToken));
            var rows = new List<SeasonTrackerContract>(sources.Count);
            for (int index = 0; index < sources.Count; index++) {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(await Row(item, sources[index], watchedBySeason[index], userId, tracker, applySavedMatches, cancellationToken));
            }
            return (account, rows);
        }
    }

    /// <summary>The seasons of the item the user syncs, in order.</summary>
    internal IReadOnlyList<LibrarySource> Sources(SeriesItem item, Guid userId) =>
        item.SourceKeys.Select(key => library.Find(key, userId)).OfType<LibrarySource>().ToList();

    /// <summary>The match that applies to the user (saved, else automatic) and the entries it points at.</summary>
    internal async Task<SeasonMatch> Resolve(LibrarySource source, Guid userId, ITrackerProvider tracker, bool applySavedMatches, CancellationToken cancellationToken) {
        Match? saved = applySavedMatches ? matches.GetEffective(userId, tracker.Name, source.Info.SourceKey, true) : null;
        if (saved != null)
            return new SeasonMatch(source, saved, saved.Scope.Owner == null ? MatchOrigin.Shared : MatchOrigin.Personal, null,
                await MatchResolver.ReadTargets(saved, tracker, cancellationToken));
        DerivedSegmentation derived = await automatic.Derive(source, tracker, cancellationToken);
        return derived.Match is { } match
            ? new SeasonMatch(source, match, MatchOrigin.Automatic, null, await MatchResolver.ReadTargets(match, tracker, cancellationToken))
            : new SeasonMatch(source, null, MatchOrigin.Automatic, derived.FailureReason ?? "not-found", new Dictionary<EntryRef, EntryState>());
    }

    private async Task<SeasonTrackerContract> Row(SeriesItem item, LibrarySource source, IReadOnlyList<WatchedEpisode> watched, Guid userId,
        ITrackerProvider tracker, bool applySavedMatches, CancellationToken cancellationToken) {
        SeasonMatch resolved;
        try {
            resolved = await Resolve(source, userId, tracker, applySavedMatches, cancellationToken);
        } catch (TrackerFailureException e) {
            logger.LogWarning("({Provider}) Could not read {SeriesName} for the progress view: {ErrorMessage}", tracker.Name, item.Name, e.Message);
            return new SeasonTrackerContract(tracker.Name, MatchOrigin.Automatic, null, e.Kind.ToString(), []);
        }
        return new SeasonTrackerContract(tracker.Name, resolved.Origin, resolved.Unresolved, null, Entries(resolved, watched));
    }

    internal static IReadOnlyList<EntryViewContract> Entries(SeasonMatch resolved, IReadOnlyList<WatchedEpisode> watched) {
        if (resolved.Match is not { } match)
            return [];
        var reached = ProgressAggregator.ForWholeSeason(match, watched.Select(episode => episode.Number), resolved.Entries)
            .ToDictionary(progress => progress.Target.Entry, progress => progress.Progress);
        var spans = new Dictionary<EntryRef, (int First, int Last)>();
        foreach (int episode in resolved.Source.Episodes) {
            if (RangeResolver.Map(match, episode) is not MappedEpisode mapped)
                continue;
            spans[mapped.Target.Entry] = spans.TryGetValue(mapped.Target.Entry, out var span) ? (Math.Min(span.First, episode), Math.Max(span.Last, episode)) : (episode, episode);
        }
        return match.Targets.Select(target => {
            EntryState? state = resolved.Entries.GetValueOrDefault(target.Entry);
            (int First, int Last)? span = spans.TryGetValue(target.Entry, out var found) ? found : null;
            return new EntryViewContract(target.Entry.Id, target.Entry.AlternativeId, state?.Titles?.Main ?? target.Title, state?.TotalEpisodes ?? 0,
                span?.First, span?.Last, state?.List?.Status, state?.List?.Progress, state?.List?.Score, state?.List?.Rewatching == true,
                reached.GetValueOrDefault(target.Entry));
        }).ToList();
    }

    private async Task<ScoreFormat?> Format(Guid userId, Guid generation, ITrackerProvider tracker, CancellationToken cancellationToken) {
        try {
            return await formats.Get(userId, generation, tracker, cancellationToken);
        } catch (TrackerFailureException e) {
            logger.LogWarning("({Provider}) Could not read the account's score format: {ErrorMessage}", tracker.Name, e.Message);
            return null;
        }
    }

    private static string? DefaultSeason(SeriesItem item, IReadOnlyList<SeasonViewContract> seasons) =>
        seasons.FirstOrDefault(season => item.OpenedSeason != null && season.Season == item.OpenedSeason)?.SourceKey
        ?? seasons.Where(season => season.LastPlayedUtc != null).MaxBy(season => season.LastPlayedUtc)?.SourceKey
        ?? seasons.FirstOrDefault()?.SourceKey;
}
