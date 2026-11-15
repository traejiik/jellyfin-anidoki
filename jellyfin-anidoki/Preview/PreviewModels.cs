using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications;
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
/// <param name="PlannedMark">Inbound only: what would be marked played.</param>
/// <param name="TrackerEntry">Inbound only: the tracker entry as read (D4 compares status, progress and finish date).</param>
/// <param name="Outcome">What the apply that claimed this row recorded for it last; null until then, and on rows not read by page.</param>
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
    [property: JsonPropertyName("appliedOperationId")] Guid? AppliedOperationId,
    [property: JsonPropertyName("plannedMark")] InboundChange? PlannedMark = null,
    [property: JsonPropertyName("trackerEntry")] InboundEntry? TrackerEntry = null,
    [property: JsonPropertyName("outcome")] AppliedOutcome? Outcome = null);

/// <summary>What an inbound row would mark played: these episodes, then the season unless an episode is ignored.</summary>
/// <param name="Unplayed">The planned episodes not yet played when previewed, as a watched fingerprint (<c>3-6</c>); null on
/// rows from before it was recorded.</param>
public sealed record InboundChange(
    [property: JsonPropertyName("seasonItem")] Guid SeasonItem,
    [property: JsonPropertyName("episodes")] IReadOnlyList<Guid> Episodes,
    [property: JsonPropertyName("playedDate")] DateTime PlayedDate,
    [property: JsonPropertyName("marksSeason")] bool MarksSeason,
    [property: JsonPropertyName("unplayed")] string? Unplayed = null);

/// <summary>The tracker entry an inbound row was planned from.</summary>
public sealed record InboundEntry(
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] jellyfin_anidoki.Models.Mal.Status? Status,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("finishDate")] string? FinishDate);

public sealed record PreviewPage(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rows")] IReadOnlyList<PreviewRow> Rows);

/// <param name="RowCounts">Rows per <see cref="PreviewRowKind"/>, by name.</param>
/// <param name="Applicable">D4: the newest cleanly finished preview for this user and direction, within 24 hours, not yet applied.</param>
/// <param name="AppliedOperationId">The apply that claimed this preview's rows; null until one did.</param>
public sealed record PreviewView(
    [property: JsonPropertyName("operation")] OperationView Operation,
    [property: JsonPropertyName("rowCounts")] IReadOnlyDictionary<string, int> RowCounts,
    [property: JsonPropertyName("applicable")] bool Applicable,
    [property: JsonPropertyName("appliedOperationId")] Guid? AppliedOperationId = null);

/// <summary>A row that can be applied, with whether it starts selected (D3): enough to select rows on pages not loaded.</summary>
public sealed record SelectableRow(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("kind")] PreviewRowKind Kind,
    [property: JsonPropertyName("selectedByDefault")] bool SelectedByDefault);

/// <summary>The rows of a preview to apply, by <see cref="PreviewRow.Seq"/>.</summary>
public sealed record ApplyRequest([property: JsonPropertyName("rows")] IReadOnlyList<int>? Rows);

/// <summary>What an apply recorded last for one preview row.</summary>
public sealed record AppliedOutcome(
    [property: JsonPropertyName("kind")] OutcomeKind Kind,
    [property: JsonPropertyName("step")] string Step);
