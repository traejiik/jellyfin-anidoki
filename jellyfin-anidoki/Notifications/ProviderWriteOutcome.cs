using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;


namespace jellyfin_anidoki.Notifications;

public enum OutcomeKind { Confirmed, NoChange, Skipped, Unresolved, Failed, Unconfirmed }

public sealed record ProviderWriteOutcome(ApiName Provider, string TargetId, string TargetTitle,
    OutcomeKind Kind, int? ConfirmedProgress, Status? ConfirmedStatus, bool StatusOnly,
    string Step, bool MeaningfulChange);
