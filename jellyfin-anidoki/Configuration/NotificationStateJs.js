const names = { AniList: 'AniList', Mal: 'MyAnimeList', Kitsu: 'Kitsu', Annict: 'Annict', Shikimori: 'Shikimori', Simkl: 'Simkl' };
const name = provider => names[provider] ?? 'Tracker';
const list = values => values.length < 2 ? values[0] : `${values.slice(0, -1).join(', ')} and ${values.at(-1)}`;
const text = value => String(value ?? '').slice(0, 256);
export const eventLifetime = 120000;
export function buildPresentation(event) {
    const outcomes = Array.isArray(event.outcomes) ? event.outcomes : [];
    const confirmed = outcomes.filter(o => o.kind === 'Confirmed' && o.meaningfulChange);
    if (!confirmed.length) return null;
    const targets = new Map();
    for (const outcome of confirmed) targets.set(`${outcome.provider}:${outcome.targetId}`, outcome);
    const finals = [...targets.values()];
    const providers = [...new Set(finals.map(o => name(o.provider)))];
    const unconfirmed = [...new Set(outcomes.filter(o => ['Failed', 'Unconfirmed'].includes(o.kind)).map(o => name(o.provider)))];
    const sameProgress = finals.every(o => !o.statusOnly && Number.isInteger(o.progress) && o.progress === finals[0].progress);
    const translated = finals.some(o => (!o.statusOnly && !event.isMovie && o.progress !== event.sourceEpisode) || text(o.targetTitle) !== text(event.sourceTitle));
    const sameStatus = finals.every(o => o.status === finals[0].status);
    const completed = finals.every(o => !o.statusOnly && o.status === 'Completed');
    const statusOnly = finals.every(o => o.statusOnly);
    let heading = completed ? `Completed on ${list(providers)}` : statusOnly ? `Status updated on ${list(providers)}` : `Logged to ${list(providers)}`;
    if ((!sameProgress && !statusOnly) || !sameStatus || translated || event.summarized) heading = 'Tracker update';
    if (unconfirmed.length) heading = `Updated on ${list(providers)}`;
    const detail = statusOnly ? list([...new Set(finals.map(o => o.status?.replaceAll('_', ' ') ?? 'Status updated'))])
        : sameProgress && !translated && !event.isMovie ? `Episode ${finals[0].progress}` : event.isMovie ? (completed ? 'Completed' : 'Movie updated') : '';
    const details = ((!sameProgress && !statusOnly) || !sameStatus || translated || event.summarized) ? finals.map(o =>
        `${name(o.provider)} · ${text(o.targetTitle)} · ${o.statusOnly ? (o.status ?? 'Status updated') : Number.isInteger(o.progress) && !event.isMovie ? `Episode ${o.progress}${o.status ? ` · ${o.status.replaceAll('_', ' ')}` : ''}` : (o.status ?? 'Updated')}`) : [];
    if (event.summarized) details.push('Additional tracker results omitted.');
    return { heading, title: text(event.sourceTitle) || 'Tracker update', detail, details, providers,
        warning: unconfirmed.length ? `${list(unconfirmed)} update could not be confirmed` : '', completed };
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
