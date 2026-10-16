using System;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Notifications;

namespace jellyfin_anidoki.ManualMarks;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualMarkState {
    Pending, Queued, Sending, Confirmed, Partial, NoChange, Skipped, Unresolved,
    Failed, Unconfirmed, Cancelled, Dismissed, Expired, Superseded
}

internal sealed record ManualMarkRequest(Guid OperationId, Guid UserId, string AuthenticationKey,
    Guid SourceItemId, string SourceTitle, int? SourceEpisode, string EpisodeTitle,
    DateTimeOffset CreatedUtc, string AccountFingerprint);

public sealed record PublicManualMarkOperation(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("sourceItemId")] Guid SourceItemId,
    [property: JsonPropertyName("sourceTitle")] string SourceTitle,
    [property: JsonPropertyName("sourceEpisode")] int? SourceEpisode,
    [property: JsonPropertyName("episodeTitle")] string EpisodeTitle,
    [property: JsonPropertyName("state")] ManualMarkState State,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc,
    [property: JsonPropertyName("outcomes")] PublicOutcome[] Outcomes,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record ManualMarkCommand([property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
public sealed record ManualMarkReadResponse(
    [property: JsonPropertyName("manualUpdatesEnabled")] bool ManualUpdatesEnabled,
    [property: JsonPropertyName("operations")] PublicManualMarkOperation[] Operations);
