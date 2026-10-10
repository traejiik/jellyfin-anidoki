#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Notifications;

// A collector belongs to one explicit playback operation. It does no provider I/O itself.
public sealed class PlaybackOutcomeCollector {
    private readonly List<ProviderWriteOutcome> _outcomes = new();
    public ProviderWriteOutcome[] Outcomes => _outcomes.ToArray();
    public bool HasConfirmedChange => _outcomes.Any(outcome => outcome.Kind == OutcomeKind.Confirmed && outcome.MeaningfulChange);

    public void Record(ProviderWriteOutcome outcome) => _outcomes.Add(outcome);

    public async Task<UpdateAnimeStatusResponse?> ObserveWrite(ApiName provider, Anime target,
        int requestedProgress, Status requestedStatus, string step,
        Func<Task<UpdateAnimeStatusResponse?>> write, bool meaningfulChange = true) {
        bool statusOnly = provider == ApiName.Annict;
        ProviderWriteOutcome Outcome(OutcomeKind kind, int? progress = null, Status? status = null, bool changed = false) =>
            new(provider, target.AlternativeId ?? target.Id.ToString(), target.Title ?? target.AlternativeTitles?.En ?? "",
                kind, statusOnly ? null : progress, status, statusOnly, step, changed);
        UpdateAnimeStatusResponse? receipt;
        try {
            receipt = await write();
        } catch {
            Record(Outcome(OutcomeKind.Unconfirmed));
            throw;
        }
        if (receipt == null) {
            Record(Outcome(OutcomeKind.Unconfirmed));
        } else {
            int? progress = statusOnly ? null : receipt.UsesAcknowledgementFields ? receipt.AcknowledgedProgress : receipt.NumEpisodesWatched;
            Status? status = receipt.UsesAcknowledgementFields ? receipt.AcknowledgedStatus : receipt.Status;
            bool? rewatching = receipt.UsesAcknowledgementFields ? receipt.AcknowledgedRewatching : receipt.IsRewatching;
            int? rewatchCount = receipt.UsesAcknowledgementFields ? receipt.AcknowledgedRewatchCount : receipt.NumTimesRewatched;
            bool changed = meaningfulChange && (target.MyListStatus == null ||
                (progress.HasValue && progress != target.MyListStatus.NumEpisodesWatched) ||
                (status.HasValue && status != target.MyListStatus.Status) ||
                (!statusOnly && rewatching.HasValue && rewatching != target.MyListStatus.IsRewatching) ||
                (!statusOnly && rewatchCount.HasValue && rewatchCount != (target.MyListStatus.RewatchCount ?? 0)));
            Record(Outcome(changed ? OutcomeKind.Confirmed : OutcomeKind.NoChange, progress, status, changed));
        }
        return receipt;
    }
}
