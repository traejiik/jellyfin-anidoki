using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Preview;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreviewDirection { ToTrackers, FromTrackers }

/// <summary>D3. Only Change and Reduction can be selected; only Change is selected by default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreviewRowKind { Change, Reduction, NoChange, Skipped, Unresolved, Failed }

/// <summary>
/// One target of one season on one tracker, as planned. Everything an apply needs to revalidate it (D4) is kept: the match
/// revision and setting, the watched fingerprint, and the entry as read.
/// </summary>
/// <param name="Reason">An existing step string (progress, already-watched, match-ignored, rate-limited…), or reduction,
/// completed-on-tracker, dropped-on-tracker.</param>
/// <param name="MatchRevision">The effective saved match's revision for this season and tracker when previewed; null when none.</param>
/// <param name="PlannedChange">What would be written; null unless the row is a Change or Reduction.</param>
/// <param name="PlannedAgainst">The entry as read, before quirks and without titles; null when it was not read.</param>
public sealed record PreviewRow(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("direction")] PreviewDirection Direction,
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("sourceItem")] Guid SourceItem,
    [property: JsonPropertyName("sourceTitle")] string SourceTitle,
    [property: JsonPropertyName("sourceEpisode")] int? SourceEpisode,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("targetTitle")] string TargetTitle,
    [property: JsonPropertyName("targetEpisode")] int? TargetEpisode,
    [property: JsonPropertyName("kind")] PreviewRowKind Kind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("resolvedBy")] ResolvedBy? ResolvedBy,
    [property: JsonPropertyName("resolutionNote")] string? ResolutionNote,
    [property: JsonPropertyName("matchRevision")] long? MatchRevision,
    [property: JsonPropertyName("applySavedMatches")] bool ApplySavedMatches,
    [property: JsonPropertyName("watchedFingerprint")] string? WatchedFingerprint,
    [property: JsonPropertyName("plannedChange")] EntryChange? PlannedChange,
    [property: JsonPropertyName("plannedAgainst")] EntryState? PlannedAgainst,
    [property: JsonPropertyName("selectable")] bool Selectable,
    [property: JsonPropertyName("selectedByDefault")] bool SelectedByDefault,
    [property: JsonPropertyName("appliedOperationId")] Guid? AppliedOperationId);

public sealed record PreviewPage(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rows")] IReadOnlyList<PreviewRow> Rows);

/// <param name="RowCounts">Rows per <see cref="PreviewRowKind"/>, by name.</param>
/// <param name="Applicable">D4: the newest cleanly finished preview for this user and direction, within 24 hours, not yet applied.</param>
public sealed record PreviewView(
    [property: JsonPropertyName("operation")] OperationView Operation,
    [property: JsonPropertyName("rowCounts")] IReadOnlyDictionary<string, int> RowCounts,
    [property: JsonPropertyName("applicable")] bool Applicable);
