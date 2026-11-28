import { list, outcomeLines, providerName } from './outcome-lines.js';

const text = value => String(value ?? '').slice(0, 256);
export const eventLifetime = 120000;
/**
 * The top says what was watched in Jellyfin; `lines` (the results box) say what each tracker recorded.
 * Only a confirmed change produces a toast. Skips stay out of the box until skip toasts arrive.
 */
export function buildPresentation(event) {
    const outcomes = Array.isArray(event.outcomes) ? event.outcomes : [];
    const changed = outcomes.filter(o => o.kind === 'Confirmed' && o.meaningfulChange);
    if (!changed.length) return null;
    const providers = [...new Set(changed.map(o => providerName(o.provider)))];
    const completed = changed.every(o => !o.statusOnly && o.status === 'Completed');
    const statusOnly = changed.every(o => o.statusOnly);
    const heading = completed ? `Completed on ${list(providers)}` : statusOnly ? `Status updated on ${list(providers)}` : `Logged to ${list(providers)}`;
    const shown = outcomes.filter(o => o.kind !== 'NoChange' && o.kind !== 'Skipped' && (o.kind !== 'Confirmed' || o.meaningfulChange));
    return {
        tone: 'success', icon: 'check', heading,
        title: text(event.sourceTitle) || 'Tracker update',
        detail: !event.isMovie && Number.isInteger(event.sourceEpisode) ? `Episode ${event.sourceEpisode}` : '',
        lines: outcomeLines(shown, event.sourceTitle, { isMovie: Boolean(event.isMovie), withNotes: true }),
        note: event.summarized ? 'Additional tracker results omitted.' : ''
    };
}
export function createNotificationState(now = Date.now) {
    const seen = new Map();
    const state = { cursor: null, enabled: true, visible: [], pending: [] };
    const fresh = event => Number.isFinite(Date.parse(event.createdUtc)) && now() - Date.parse(event.createdUtc) < eventLifetime;
    state.clear = () => { state.visible = []; state.pending = []; seen.clear(); };
    state.accept = event => {
        for (const [id, expires] of seen) if (expires <= now()) seen.delete(id);
        if (!fresh(event) || !buildPresentation(event) || !event.eventId || !event.operationId || seen.has('e:' + event.eventId) || seen.has('o:' + event.operationId)) return false;
        seen.set('e:' + event.eventId, now() + eventLifetime); seen.set('o:' + event.operationId, now() + eventLifetime);
        while (seen.size > 4096) seen.delete(seen.keys().next().value);
        if (state.visible.length < 2) state.visible.unshift(event);
        else { state.pending.push(event); if (state.pending.length > 10) state.pending.shift(); }
        return true;
    };
    state.dismiss = id => {
        state.visible = state.visible.filter(event => event.eventId !== id);
        state.pending = state.pending.filter(fresh);
        while (state.visible.length < 2 && state.pending.length) state.visible.unshift(state.pending.shift());
    };
    state.receive = response => {
        if (response.reset || !response.enabled || !state.enabled) state.clear();
        state.cursor = response.cursor; state.enabled = response.enabled;
        if (response.enabled && !response.reset) for (const event of response.events ?? []) state.accept(event);
    };
    return state;
}
export function createVisibleTimer(now = () => performance.now(), duration = 6000) {
    let left = duration, started = now(); const reasons = new Set();
    const remaining = () => Math.max(0, left - (reasons.size ? 0 : now() - started));
    return { remaining, pause(reason) { if (!reasons.size) left = remaining(); reasons.add(reason); },
        resume(reason) { const had = reasons.delete(reason); if (had && !reasons.size) started = now(); },
        get paused() { return reasons.size !== 0; } };
}
