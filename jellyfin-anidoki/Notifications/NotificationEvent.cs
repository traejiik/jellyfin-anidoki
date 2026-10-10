#nullable enable
using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace jellyfin_anidoki.Notifications;

public sealed record NotificationEvent(Guid EventId, Guid OperationId, long Sequence, DateTimeOffset CreatedUtc,
    Guid UserId, string SessionId, Guid SourceItemId, string SourceTitle, int? SourceEpisode, bool IsMovie,
    ProviderWriteOutcome[] ProviderOutcomes);

public sealed record PublicOutcome(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("targetTitle")] string TargetTitle,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("statusOnly")] bool StatusOnly,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("meaningfulChange")] bool MeaningfulChange);

public sealed record PublicNotificationEvent(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("sourceItemId")] Guid SourceItemId,
    [property: JsonPropertyName("sourceTitle")] string SourceTitle,
    [property: JsonPropertyName("sourceEpisode")] int? SourceEpisode,
    [property: JsonPropertyName("isMovie")] bool IsMovie,
    [property: JsonPropertyName("outcomes")] PublicOutcome[] Outcomes,
    [property: JsonPropertyName("summarized")] bool Summarized)
{
    internal static string Bounded(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(256, text.Length)];
    private static ProviderWriteOutcome[] Sample(ProviderWriteOutcome[] outcomes)
    {
        if (outcomes.Length <= 24) return outcomes;
        var representatives = outcomes.GroupBy(o => (o.Provider, o.Kind)).Select(g => g.FirstOrDefault(o => o.Kind == OutcomeKind.Confirmed && o.MeaningfulChange) ?? g.First())
            .OrderBy(o => o.Kind == OutcomeKind.Confirmed ? 0 : o.Kind is OutcomeKind.Unconfirmed or OutcomeKind.Failed ? 1 : 2).ToArray();
        return representatives.Concat(outcomes).Distinct().Take(24).ToArray();
    }
    internal static PublicNotificationEvent From(NotificationEvent item) => new(item.EventId, item.OperationId,
        item.CreatedUtc, item.SourceItemId, Bounded(item.SourceTitle), item.SourceEpisode, item.IsMovie,
        Sample(item.ProviderOutcomes).Select(outcome => new PublicOutcome(outcome.Provider.ToString(), Bounded(outcome.TargetId),
            Bounded(outcome.TargetTitle), outcome.Kind.ToString(), outcome.ConfirmedProgress, outcome.ConfirmedStatus?.ToString(),
            outcome.StatusOnly, Bounded(outcome.Step), outcome.MeaningfulChange)).ToArray(), item.ProviderOutcomes.Length > 24);
}

public sealed record NotificationReadResponse(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("reset")] bool Reset,
    [property: JsonPropertyName("events")] PublicNotificationEvent[] Events);
