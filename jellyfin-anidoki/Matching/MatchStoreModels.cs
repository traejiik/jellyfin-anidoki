using System;
using System.Collections.Generic;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Matching;

public enum MatchWriteStatus { Written, Conflict, NotFound }

/// <summary>The result of a save or delete.</summary>
/// <param name="Revision">After a write, the revision it recorded. Otherwise the current revision, 0 when none exists.</param>
public sealed record MatchWrite(MatchWriteStatus Status, long Revision);

/// <summary>The result of a promotion. Each revision is the one now current for that scope, 0 when none exists.</summary>
public sealed record MatchPromotion(MatchWriteStatus Status, long SharedRevision, long OverrideRevision);

/// <summary>A match as stored, with what the editor shows beside it.</summary>
/// <param name="ConnectionGeneration">Personal only, for display: the provider link this was saved under.</param>
public sealed record StoredMatch(Match Match, string? Note, string UpdatedBy, DateTimeOffset UpdatedUtc, string? ConnectionGeneration);

public enum MatchRevisionAction { Save, Delete, Promote }

/// <summary>The ranges and exceptions of one revision; both empty for a delete.</summary>
public sealed record MatchSnapshot(IReadOnlyList<MatchRange> Ranges, IReadOnlyList<MatchException> Exceptions);

/// <param name="PromotedFrom">For a promotion, "{userId}:{revision}" of the override it came from.</param>
public sealed record MatchHistoryEntry(long Revision, MatchRevisionAction Action, string Author, DateTimeOffset CreatedUtc,
    MatchSnapshot Snapshot, string? PromotedFrom);

/// <summary>One source and what is saved for it now.</summary>
/// <param name="Shared">The shared revision per provider.</param>
/// <param name="Overrides">How many personal overrides per provider.</param>
public sealed record MatchSourceSummary(MatchSource Source, DateTimeOffset UpdatedUtc, IReadOnlyDictionary<ApiName, long> Shared,
    IReadOnlyDictionary<ApiName, int> Overrides);

/// <summary>One personal override, without its ranges.</summary>
public sealed record OverrideSummary(MatchScope Scope, long Revision, long? BaseSharedRevision);
