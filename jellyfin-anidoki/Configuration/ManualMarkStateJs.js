import { outcomeLines } from './outcome-lines.js';

const active = new Set(['Pending', 'Queued', 'Sending']);
const headings = { Pending: 'Update trackers?', Queued: 'Tracker update queued', Sending: 'Updating trackers…', Confirmed: 'Trackers updated', Partial: 'Some trackers updated', NoChange: 'No tracker changes', Skipped: 'Tracker update skipped', Unresolved: 'Tracker match unresolved', Failed: 'Tracker update failed', Unconfirmed: 'Tracker response unconfirmed', Cancelled: 'Tracker update cancelled', Dismissed: 'Tracker update dismissed', Expired: 'Confirmation expired', Superseded: 'Updated by completed playback' };
// Dismissing is the viewer's choice, so it is neutral rather than an error.
const tones = { Pending: 'active', Queued: 'active', Sending: 'active', Confirmed: 'success', Superseded: 'success', Partial: 'attention', Skipped: 'attention', Unresolved: 'attention', Unconfirmed: 'attention', Failed: 'error', NoChange: 'neutral', Cancelled: 'neutral', Dismissed: 'neutral', Expired: 'neutral' };
const icons = { Queued: 'clock', Confirmed: 'check', Superseded: 'check', Partial: 'triangle', NoChange: 'dash', Skipped: 'skip', Unresolved: 'question', Failed: 'cross', Unconfirmed: 'triangle' };
// The results box already says what happened here, so the server's sentence would only repeat the heading.
const linesSpeak = new Set(['Confirmed', 'Partial', 'NoChange', 'Skipped']);
// These explain the result, so they read after it.
const afterLines = new Set(['Unresolved', 'Failed', 'Unconfirmed']);
export function buildManualMarkPresentation(operation) {
    const state = operation.state;
    return { lines: outcomeLines(operation.outcomes, operation.sourceTitle), heading: headings[state] ?? 'Tracker update',
        tone: tones[state] ?? 'neutral', icon: icons[state] ?? '', terminal: !active.has(state), spinner: state === 'Sending',
        detailAfterLines: afterLines.has(state),
        detail: linesSpeak.has(state) ? '' : operation.reason || (state === 'Pending' ? 'Jellyfin is marked watched. Confirm within two minutes to update trackers.' : state === 'Sending' ? 'Updating has already started. Closing this card does not undo a transmitted update.' : '') };
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
