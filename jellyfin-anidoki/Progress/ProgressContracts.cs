using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;

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
    [property: JsonPropertyName("entries")] IReadOnlyList<EntryViewContract> Entries);

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
