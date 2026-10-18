using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace jellyfin_anidoki.Operations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationKind { Playback, WatchedToggle, ConfirmedManual, SyncToTrackers, SyncFromTrackers }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationState { Queued, Running, Succeeded, Partial, NoChange, Skipped, Unresolved, Unconfirmed, Failed, Cancelled, Interrupted }

public static class OperationStates {
    public static bool IsFinished(OperationState state) => state is not (OperationState.Queued or OperationState.Running);

    /// <summary>The overall result of a run from its outcomes; the same rules manual marks use. No outcomes means nothing needed changing.</summary>
    public static OperationState FromOutcomes(IReadOnlyCollection<ProviderWriteOutcome> outcomes) {
        if (outcomes.Count == 0)
            return OperationState.NoChange;
        if (outcomes.Any(outcome => outcome.Kind == OutcomeKind.Confirmed && outcome.MeaningfulChange))
            return outcomes.Any(outcome => outcome.Kind is OutcomeKind.Failed or OutcomeKind.Unconfirmed or OutcomeKind.Unresolved)
                ? OperationState.Partial
                : OperationState.Succeeded;
        if (outcomes.Any(outcome => outcome.Kind == OutcomeKind.Unconfirmed))
            return OperationState.Unconfirmed;
        if (outcomes.Any(outcome => outcome.Kind == OutcomeKind.Failed))
            return OperationState.Failed;
        if (outcomes.Any(outcome => outcome.Kind == OutcomeKind.NoChange))
            return OperationState.NoChange;
        if (outcomes.Any(outcome => outcome.Kind == OutcomeKind.Skipped))
            return OperationState.Skipped;
        return OperationState.Unresolved;
    }
}

public sealed record OperationView(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("kind")] OperationKind Kind,
    [property: JsonPropertyName("state")] OperationState State,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset? StartedUtc,
    [property: JsonPropertyName("finishedUtc")] DateTimeOffset? FinishedUtc,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("done")] int Done,
    [property: JsonPropertyName("total")] int? Total,
    [property: JsonPropertyName("counts")] IReadOnlyDictionary<string, int> Counts);

public sealed record OperationStarted([property: JsonPropertyName("operationId")] Guid OperationId);

internal static class OperationSources {
    /// <summary>A short label for what an operation is about, shown with its result.</summary>
    public static string Describe(BaseItem item) => item is Episode episode
        ? $"{episode.SeriesName ?? "Unknown series"} · episode {episode.IndexNumber?.ToString() ?? "?"}"
        : item.Name ?? "Unknown item";
}
