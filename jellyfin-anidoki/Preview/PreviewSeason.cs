using System;
using System.Globalization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Execution;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Planning;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Entities.TV;

namespace jellyfin_anidoki.Preview;

/// <summary>What every row of one season on one tracker shares, and how each plan step becomes a row.</summary>
/// <param name="MatchRevision">The effective saved match's revision when previewed; null when there is none.</param>
internal sealed record PreviewSeason(ApiName Provider, SyncSource Source, long? MatchRevision, bool ApplySavedMatches, string Fingerprint) {
    public PreviewRow FromTarget(PlannedTarget planned, ProviderCapabilities capabilities) {
        ResolvedTarget target = planned.Trace.Target;
        (PreviewRowKind Kind, string Reason, EntryChange? Change) row = planned.Decision switch {
            Write write => (PreviewRowKind.Change, write.Reason, write.Change),
            // D3: only a decision not to write can become a reduction; a skip keeps its own reason.
            NoChange noChange => ReductionCheck.Check(planned.Read, target.TargetEpisode, capabilities) switch {
                LowerProgress lower => (PreviewRowKind.Reduction, ReductionCheck.Reason, lower.Change),
                KeepTrackerProgress keep => (PreviewRowKind.NoChange, keep.Reason, (EntryChange?)null),
                _ => (PreviewRowKind.NoChange, noChange.Reason, (EntryChange?)null)
            },
            Skip skip => (PreviewRowKind.Skipped, skip.Reason, (EntryChange?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(planned), planned.Decision, "Unknown plan decision.")
        };
        return Row(TargetId(target.Entry), target.Title, target.TargetEpisode, row.Kind, row.Reason, planned.Trace.By, planned.Trace.Note,
            row.Change, planned.Read with { Titles = null });
    }

    /// <summary>Unresolved, ignored, unreadable, or a tracker failure.</summary>
    public PreviewRow FromOutcome(PlannedOutcome planned) {
        ProviderWriteOutcome outcome = planned.Outcome;
        PreviewRowKind kind = outcome.Kind switch {
            OutcomeKind.Skipped => PreviewRowKind.Skipped,
            OutcomeKind.Unresolved => PreviewRowKind.Unresolved,
            _ => PreviewRowKind.Failed
        };
        return Row(outcome.TargetId, outcome.TargetTitle, planned.Detail?.TargetEpisode, kind, outcome.Step, planned.Detail?.By,
            planned.Detail?.Note, null, null);
    }

    /// <summary>A season automatic sync records nothing for; the preview still says why it has no target.</summary>
    public PreviewRow Silent(Unresolved unresolved) =>
        Row("", Source.SearchTitle, null, PreviewRowKind.Unresolved, "resolve", null, unresolved.Note ?? unresolved.Reason, null, null);

    private PreviewRow Row(string targetId, string targetTitle, int? targetEpisode, PreviewRowKind kind, string reason, ResolvedBy? by,
        string? note, EntryChange? change, EntryState? against) =>
        new(0, PreviewDirection.ToTrackers, Provider, Source.SourceKey, Source.Item.Id, Title, Source.EpisodeNumber, targetId, targetTitle,
            targetEpisode, kind, reason, by, note, MatchRevision, ApplySavedMatches, Fingerprint, change, against,
            kind is PreviewRowKind.Change or PreviewRowKind.Reduction, kind == PreviewRowKind.Change, null);

    private string Title => Source.Item is Episode episode
        ? $"{episode.SeriesName ?? "Unknown series"} · season {Source.SeasonNumber.ToString(CultureInfo.InvariantCulture)}"
        : Source.SearchTitle;

    // As the executor's outcomes name a target.
    private static string TargetId(EntryRef entry) => entry.AlternativeId ?? (entry.Lookup == null ? entry.Id.ToString(CultureInfo.InvariantCulture) : "");
}
