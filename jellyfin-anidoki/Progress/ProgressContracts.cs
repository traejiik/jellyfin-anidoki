using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;

namespace jellyfin_anidoki.Progress;

/// <summary>Where a season's entries came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchOrigin { Personal, Shared, Automatic }

/// <summary>One series or movie with each linked tracker's entries per season: what the progress popup shows.</summary>
/// <param name="Images">The image types the item has; the client builds <c>/Items/{id}/Images/{type}</c>.</param>
/// <param name="DefaultSeason">The season to select first: the one opened from, else the most recently played, else the first.</param>
/// <param name="MatchesLinkAvailable">Whether the Matches tab may link to Your matches in the user settings.</param>
public sealed record SeriesViewContract(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isMovie")] bool IsMovie,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("endYear")] int? EndYear,
    [property: JsonPropertyName("communityRating")] float? CommunityRating,
    [property: JsonPropertyName("officialRating")] string? OfficialRating,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("premiereDate")] DateTime? PremiereDate,
    [property: JsonPropertyName("images")] IReadOnlyList<string> Images,
    [property: JsonPropertyName("episodeCount")] int EpisodeCount,
    [property: JsonPropertyName("defaultSeason")] string? DefaultSeason,
    [property: JsonPropertyName("matchesLinkAvailable")] bool MatchesLinkAvailable,
    [property: JsonPropertyName("trackers")] IReadOnlyList<TrackerAccountContract> Trackers,
    [property: JsonPropertyName("seasons")] IReadOnlyList<SeasonViewContract> Seasons);

/// <param name="ScoreFormat">Null when the tracker has no scores or could not say which format the account uses.</param>
public sealed record TrackerAccountContract(
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider,
    [property: JsonPropertyName("supportsScore")] bool SupportsScore,
    [property: JsonPropertyName("scoreFormat"), JsonConverter(typeof(JsonStringEnumConverter))] ScoreFormat? ScoreFormat);

/// <param name="Season">Null for a movie.</param>
/// <param name="Episodes">The episode numbers the library holds; <c>[1]</c> for a movie.</param>
/// <param name="Watched">The episode numbers the user has played.</param>
public sealed record SeasonViewContract(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("episodes")] IReadOnlyList<int> Episodes,
    [property: JsonPropertyName("watched")] IReadOnlyList<int> Watched,
    [property: JsonPropertyName("lastPlayedUtc")] DateTime? LastPlayedUtc,
    [property: JsonPropertyName("trackers")] IReadOnlyList<SeasonTrackerContract> Trackers);

/// <param name="Unresolved">Why automatic resolution found no entry; null when it did or a saved match applies.</param>
/// <param name="Error">The tracker failure that stopped the read (Unauthorized, RateLimited, Transient, Rejected); null when it worked.</param>
public sealed record SeasonTrackerContract(
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider,
    [property: JsonPropertyName("origin")] MatchOrigin Origin,
    [property: JsonPropertyName("unresolved")] string? Unresolved,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("entries")] IReadOnlyList<EntryViewContract> Entries,
    [property: JsonPropertyName("matchRanges")] IReadOnlyList<MatchRangeView> MatchRanges);

public sealed record MatchRangeView(
    [property: JsonPropertyName("sourceStart")] int SourceStart,
    [property: JsonPropertyName("sourceEnd")] int SourceEnd,
    [property: JsonPropertyName("targetStart")] int TargetStart,
    [property: JsonPropertyName("targetEnd")] int TargetEnd,
    [property: JsonPropertyName("entry")] EntryContract Entry);

/// <summary>One tracker entry within a season, with the user's list state and how far Jellyfin says they are.</summary>
/// <param name="TotalEpisodes">0 when the tracker does not know.</param>
/// <param name="FirstEpisode">The first of the season's episodes that map to this entry, for "Ep 13–24".</param>
/// <param name="Status">Null when the entry is not on the user's list.</param>
/// <param name="Score">Raw 1–100; null when unrated or the tracker has no scores.</param>
/// <param name="JellyfinProgress">The entry's own episode the user has reached in Jellyfin; 0 when nothing mapped to it is watched.</param>
public sealed record EntryViewContract(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("alternativeId")] string? AlternativeId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("totalEpisodes")] int TotalEpisodes,
    [property: JsonPropertyName("firstEpisode")] int? FirstEpisode,
    [property: JsonPropertyName("lastEpisode")] int? LastEpisode,
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))] TrackerStatus? Status,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("rewatching")] bool Rewatching,
    [property: JsonPropertyName("jellyfinProgress")] int JellyfinProgress);

/// <summary>What the popup showed for an entry when the edit was made; the edit is refused if the tracker no longer says this.</summary>
/// <param name="Status">Null when the entry was not on the list.</param>
public sealed record ShownStateContract(
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))] TrackerStatus? Status,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("score")] int? Score);

/// <summary>One entry's requested state on one tracker.</summary>
/// <param name="Score">Raw 1–100; null leaves the score as it is. Dropped for a tracker without scores.</param>
/// <param name="ClearScore">Removes the rating; never with <paramref name="Score"/>.</param>
public sealed record EntryEditContract(
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider,
    [property: JsonPropertyName("entryId")] int EntryId,
    [property: JsonPropertyName("alternativeId")] string? AlternativeId,
    [property: JsonPropertyName("shown")] ShownStateContract Shown,
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))] TrackerStatus Status,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("clearScore")] bool ClearScore);

/// <param name="ItemId">The series, season, episode or movie the popup is showing.</param>
public sealed record EditRequest(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("edits")] IReadOnlyList<EntryEditContract> Edits);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EditResultKind { Pending, Saved, Stale, Unconfirmed, Failed, NotFound }

/// <param name="Current">Stale only: the entry as the tracker now has it, to show instead.</param>
/// <param name="Failure">Failed only: unauthorized, rate-limited, transient or rejected.</param>
public sealed record EditResultContract(
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider,
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("result")] EditResultKind Result,
    [property: JsonPropertyName("current")] ShownStateContract? Current,
    [property: JsonPropertyName("failure")] string? Failure);

public sealed record EditOutcomeContract(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("results")] IReadOnlyList<EditResultContract> Results);

/// <summary>A one-series sync in one direction, through one tracker.</summary>
public sealed record SeriesSyncRequest(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))] ApiName Provider);
