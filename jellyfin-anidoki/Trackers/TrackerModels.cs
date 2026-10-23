using System;
using System.Collections.Generic;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Trackers;

/// <summary>
/// A list status, the same for every provider. A rewatch in progress is <see cref="Completed"/> with
/// <see cref="ListState.Rewatching"/> set, whatever the provider calls it.
/// </summary>
public enum TrackerStatus { Watching, Completed, OnHold, Dropped, PlanToWatch }

public enum MediaKind { Series, Movie }

public enum RelationKind { Sequel, Prequel, SideStory, AlternativeVersion, AlternativeSetting, Other }

public enum RewatchTransition { None, Start, Continue, Finish }

/// <summary>Tracker IDs resolved for one Jellyfin item.</summary>
public sealed record ResolvedIds(int? AniList, int? Mal, int? Kitsu, int? AniDb);

/// <summary>What a provider that resolves by cross IDs (Simkl) needs to read and write the entry again.</summary>
public sealed record CrossReference(ResolvedIds Ids, string Title, MediaKind Kind);

public sealed record EntryRef(ApiName Provider, int Id, string? AlternativeId, CrossReference? Lookup = null);

/// <summary>The user's list state. <see cref="RewatchCount"/> is null when the provider does not report it.</summary>
public sealed record ListState(TrackerStatus Status, int Progress, bool Rewatching, int? RewatchCount);

/// <summary>
/// An entry as read. <see cref="TotalEpisodes"/> is 0 when unknown; <see cref="List"/> is null when it is not on the list;
/// <see cref="Titles"/> is null when the provider returned none.
/// </summary>
public sealed record EntryState(EntryRef Entry, int TotalEpisodes, ListState? List, DateTimeOffset ReadAt, EntryTitles? Titles = null);

public sealed record EntryTitles(string? Main, string? English, string? Japanese, IReadOnlyList<string> Synonyms);

public sealed record EntryCandidate(EntryRef Entry, EntryTitles Titles, int TotalEpisodes);

public sealed record EntryRelation(RelationKind Kind, EntryRef Entry);

/// <summary>
/// What should be true after a write. A rewatch change (<see cref="Rewatch"/> other than None) must target
/// <see cref="TrackerStatus.Completed"/>: Start and Continue leave a rewatch in progress at <see cref="Progress"/>,
/// Finish completes it and counts it once.
/// </summary>
public sealed record EntryChange(int Progress, TrackerStatus Status, RewatchTransition Rewatch = RewatchTransition.None,
    DateTime? StartDate = null, DateTime? EndDate = null);

/// <summary>What the provider acknowledged. <see cref="Complete"/> is false when a multi-call write stopped part way.</summary>
public sealed record WriteReceipt(int? Progress, TrackerStatus? Status, bool? Rewatching, int? RewatchCount, bool Complete);
