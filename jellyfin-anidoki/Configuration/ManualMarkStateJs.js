const active = new Set(['Pending', 'Queued', 'Sending']);
const headings = { Pending: 'Update trackers?', Queued: 'Tracker update queued', Sending: 'Updating trackers…', Confirmed: 'Trackers updated', Partial: 'Some trackers updated', NoChange: 'No tracker changes', Skipped: 'Tracker update skipped', Unresolved: 'Tracker match unresolved', Failed: 'Tracker update failed', Unconfirmed: 'Tracker response unconfirmed', Cancelled: 'Tracker update cancelled', Dismissed: 'Tracker update dismissed', Expired: 'Confirmation expired', Superseded: 'Updated by completed playback' };
export function buildManualMarkPresentation(operation) {
    return { heading: headings[operation.state] ?? 'Tracker update', terminal: !active.has(operation.state), spinner: operation.state === 'Sending',
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
