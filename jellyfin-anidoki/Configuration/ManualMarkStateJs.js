const active = new Set(['Pending', 'Queued', 'Sending']);
const headings = { Pending: 'Update trackers?', Queued: 'Tracker update queued', Sending: 'Updating trackers…', Confirmed: 'Trackers updated', Partial: 'Some trackers updated', NoChange: 'No tracker changes', Skipped: 'Tracker update skipped', Unresolved: 'Tracker match unresolved', Failed: 'Tracker update failed', Unconfirmed: 'Tracker response unconfirmed', Cancelled: 'Tracker update cancelled', Dismissed: 'Tracker update dismissed', Expired: 'Confirmation expired', Superseded: 'Updated by completed playback' };
const providers = { AniList: 'AniList', Mal: 'MyAnimeList', Kitsu: 'Kitsu', Annict: 'Annict', Shikimori: 'Shikimori', Simkl: 'Simkl' };
const reasons = { 'already-watched': 'Episode already watched', 'manual-forward-only': 'Already at or past this episode', 'rewatch-disabled': 'Skipped: completed, and rewatching is off',
    'rewatch-unsupported': 'Skipped: rewatching is not supported', 'plan-to-watch-only': 'Skipped: not on Plan to Watch' };
const results = { NoChange: 'Already up to date', Skipped: 'Skipped', Unresolved: 'No matching entry found', Failed: 'Update failed', Unconfirmed: 'Update not confirmed' };
function describeOutcome(outcome) {
    const status = outcome.status?.replaceAll('_', ' ');
    const result = outcome.kind === 'Confirmed' ? (outcome.statusOnly || outcome.progress == null ? status ?? 'Updated' : '') : reasons[outcome.step] ?? results[outcome.kind] ?? 'Updated';
    const episode = !outcome.statusOnly && Number.isInteger(outcome.progress) ? `Episode ${outcome.progress}` : '';
    return [providers[outcome.provider] ?? 'Tracker', result, episode, outcome.kind === 'Confirmed' && episode ? status : ''].filter(Boolean).join(' · ');
}
export function buildManualMarkPresentation(operation) {
    return { outcomes: (operation.outcomes ?? []).map(describeOutcome), heading: headings[operation.state] ?? 'Tracker update', terminal: !active.has(operation.state), spinner: operation.state === 'Sending',
        detail: operation.reason || (operation.state === 'Pending' ? 'Jellyfin is marked watched. Confirm within two minutes to update trackers.' : operation.state === 'Sending' ? 'Updating has already started. Closing this card does not undo a transmitted update.' : '') };
}
export function createManualMarkState() {
    const operations = new Map(), hidden = new Set();
    return { operations,
        upsert(operation) { if (!operation?.operationId || !Number.isSafeInteger(operation.revision)) return false; const existing = operations.get(operation.operationId); if (existing && existing.revision >= operation.revision) return false; if (!existing && operations.size >= 256) {
            const retired = [...operations.values()].find(o => !active.has(o.state));
            if (!retired) return false;
            operations.delete(retired.operationId); hidden.delete(retired.operationId);
        }
        operations.set(operation.operationId, operation); return true; },
        visible() { return [...operations.values()].filter(o => !hidden.has(o.operationId)).sort((a,b) => Number(!active.has(a.state))-Number(!active.has(b.state))).slice(0,2); },
        hide(id) { hidden.add(id); }, clear() { operations.clear(); hidden.clear(); }
    };
}
