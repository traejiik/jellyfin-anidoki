import { providerName } from './outcome-lines.js';

// The progress popup's rules, kept apart from the DOM so Node can test them. Views are SeriesViewContract
// (Progress/ProgressContracts.cs); scores are raw 1–100, as the tracker contracts carry them.

// --- Scores: a port of Trackers/ScoreScale.cs ---------------------------------------------------------------

const scales = {
    Point100: { max: 100, step: 1, min: 1 },
    Point10Decimal: { max: 10, step: 0.1, min: 0.1 },
    Point10Half: { max: 10, step: 0.5, min: 1 },
    Point10: { max: 10, step: 1, min: 1 },
    Point5: { max: 5, step: 1, min: 1 },
    Point3: { max: 3, step: 1, min: 1 }
};
const point3 = [35, 60, 85];
export const faceNames = ['Disliked', 'Neutral', 'Liked'];

export const scoreScale = format => ({ ...scales[format] });

export const sameScale = (a, b) => scales[a]?.max === scales[b]?.max;

export function toDisplay(raw, format) {
    if (raw === null || raw === undefined)
        return null;
    switch (format) {
        case 'Point100': return raw;
        case 'Point10Decimal': return Math.round(raw) / 10;
        case 'Point10Half': return Math.max(1, Math.round(raw / 5) / 2);
        case 'Point10': return Math.max(1, Math.round(raw / 10));
        case 'Point5': return Math.max(1, Math.round(raw / 20));
        case 'Point3': return raw <= point3[0] ? 1 : raw <= point3[1] ? 2 : 3;
        default: return null;
    }
}

/** The raw score for a shown value, or null when the value is not a score in the format. */
export function toRaw(display, format) {
    const scale = scales[format];
    if (display === null || display === undefined || !scale || !Number.isFinite(display))
        return null;
    const steps = display / scale.step;
    if (display < scale.min || display > scale.max || Math.abs(steps - Math.round(steps)) > 1e-9)
        return null;
    switch (format) {
        case 'Point100': return Math.round(display);
        case 'Point5': return Math.round(display) * 20;
        case 'Point3': return point3[Math.round(display) - 1];
        default: return Math.round(display * 10);
    }
}

export function scoreText(raw, format) {
    const display = toDisplay(raw, format);
    if (display === null)
        return 'no rating';
    if (format === 'Point3')
        return faceNames[display - 1];
    return `${display}/${scales[format].max}`;
}

// --- Statuses ----------------------------------------------------------------------------------------------

const statusNames = { Watching: 'Watching', Completed: 'Completed', Dropped: 'Dropped', PlanToWatch: 'Plan to watch' };
export const statusOrder = ['Watching', 'Completed', 'PlanToWatch', 'OnHold', 'Dropped'];

/** AniList calls on hold "Paused"; every other tracker says "On hold". */
export function statusLabel(status, provider) {
    if (status === null || status === undefined)
        return 'Not on your list';
    if (status === 'OnHold')
        return provider === 'AniList' ? 'Paused' : 'On hold';
    return statusNames[status] ?? status;
}

export function sharedStatusLabel(status, providers) {
    if (status === 'OnHold' && providers.includes('AniList') && providers.some(provider => provider !== 'AniList'))
        return 'Paused or on hold';
    return statusLabel(status, providers[0]);
}

// --- Seasons rows (D6) -------------------------------------------------------------------------------------

const range = (first, last) => first === last ? `${first}` : `${first}–${last}`;
export const seasonName = number => number === null || number === undefined ? 'Movie' : number === 0 ? 'Specials' : `Season ${number}`;
const first = entry => entry.firstEpisode ?? 1;

/** The tracker whose entries split the season most finely; the view's tracker order breaks ties. */
function splitter(view, season) {
    let best = null;
    for (const account of view.trackers) {
        const tracker = season.trackers.find(item => item.provider === account.provider);
        if (tracker && !tracker.error && tracker.entries.length > (best?.entries.length ?? 0))
            best = tracker;
    }
    return best;
}

/**
 * One row per season, or, where a tracker splits a season into several entries, one row per entry, labelled by the
 * Jellyfin episodes it covers. Each row's key is stable across reloads: the season's source key and its first episode.
 */
export function buildRows(view) {
    const rows = [];
    for (const season of view.seasons) {
        const name = view.isMovie ? 'Movie' : seasonName(season.season);
        const tracker = splitter(view, season);
        if (!tracker || tracker.entries.length < 2) {
            const start = season.episodes[0] ?? 1;
            rows.push({ key: `${season.sourceKey}:${start}`, sourceKey: season.sourceKey, season: season.season, label: name, group: null,
                heading: null, firstEpisode: start, lastEpisode: season.episodes.at(-1) ?? start, split: false });
            continue;
        }
        for (const entry of [...tracker.entries].sort((a, b) => first(a) - first(b))) {
            const episodes = range(first(entry), entry.lastEpisode ?? first(entry));
            rows.push({ key: `${season.sourceKey}:${first(entry)}`, sourceKey: season.sourceKey, season: season.season, label: `Ep ${episodes}`,
                group: name, heading: `${name}, episodes ${episodes}: ${entry.title}`, firstEpisode: first(entry),
                lastEpisode: entry.lastEpisode ?? first(entry), split: true });
        }
    }
    return rows;
}

export function defaultRow(view, rows) {
    return rows.find(row => row.sourceKey === view.defaultSeason) ?? rows[0] ?? null;
}

const covers = (entry, episode) => first(entry) <= episode && episode <= (entry.lastEpisode ?? Infinity);

/** Each linked tracker's entry for a row, or why it has none. In the view's tracker order. */
export function rowTargets(view, row) {
    const season = view.seasons.find(item => item.sourceKey === row?.sourceKey);
    return view.trackers.map(account => {
        const tracker = season?.trackers.find(item => item.provider === account.provider);
        const base = { provider: account.provider, account, entry: null, state: 'unmatched', error: null };
        if (!tracker)
            return base;
        if (tracker.error)
            return { ...base, state: 'error', error: tracker.error };
        const entry = tracker.entries.length === 1 ? tracker.entries[0] : tracker.entries.find(item => covers(item, row.firstEpisode));
        return entry ? { ...base, entry, state: 'ok' } : base;
    });
}

export const editableTargets = targets => targets.filter(target => target.state === 'ok');

// --- Pills -------------------------------------------------------------------------------------------------

const count = (progress, total) => `${progress}/${total || '?'}`;

/** The saved state of a target's entry; an entry not on the list has no status and no progress. */
export const savedState = target => ({ status: target.entry?.status ?? null, progress: target.entry?.progress ?? 0, score: target.entry?.score ?? null });

/** What a closed pill shows: the status, a bar's progress and total, and the count. A draft shows instead of the saved state. */
export function pillSummary(target, draft) {
    if (target.state === 'error')
        return { status: `Couldn't read ${providerName(target.provider)}`, progress: 0, total: 0, text: '' };
    if (target.state === 'unmatched')
        return { status: 'No match for this season', progress: 0, total: 0, text: '' };
    const shown = draft ?? savedState(target), total = target.entry.totalEpisodes;
    return { status: statusLabel(shown.status, target.provider), progress: shown.progress, total, text: count(shown.progress, total) };
}

/** The All trackers pill: the common status or "Mixed", and the count or the range of counts. */
export function allSummary(targets, drafts) {
    const shown = targets.map(target => drafts[target.provider] ?? savedState(target));
    const statuses = new Set(shown.map(item => item.status));
    const progress = shown.map(item => item.progress), totals = new Set(targets.map(target => target.entry.totalEpisodes));
    const low = Math.min(...progress), high = Math.max(...progress), total = totals.size === 1 ? [...totals][0] : 0;
    return {
        status: statuses.size === 1 ? sharedStatusLabel(shown[0].status, targets.map(target => target.provider)) : 'Mixed',
        text: `${range(low, high)}/${total || '?'}`
    };
}

// --- Details box -------------------------------------------------------------------------------------------

const animeSeasons = ['Winter', 'Winter', 'Winter', 'Spring', 'Spring', 'Spring', 'Summer', 'Summer', 'Summer', 'Fall', 'Fall', 'Fall'];

/** Anime seasons by month: January–March is Winter, then Spring, Summer and Fall. */
export function airedLabel(premiereDate) {
    if (!premiereDate)
        return null;
    const date = new Date(premiereDate);
    return Number.isNaN(date.getTime()) ? null : `${animeSeasons[date.getUTCMonth()]} ${date.getUTCFullYear()}`;
}

export const seriesStatusLabel = status => ({ Continuing: 'Airing', Ended: 'Finished' })[status] ?? null;

const entryPages = {
    AniList: 'https://anilist.co/anime/', Mal: 'https://myanimelist.net/anime/', Kitsu: 'https://kitsu.app/anime/',
    Shikimori: 'https://shikimori.io/animes/', Simkl: 'https://simkl.com/anime/', Annict: 'https://annict.com/works/'
};

export const entryUrl = (provider, entry) => entryPages[provider] && entry ? entryPages[provider] + encodeURIComponent(entry.id) : null;

// --- Editor drafts -----------------------------------------------------------------------------------------
// A draft is what the user wants an entry to become: { status, progress, score, clearScore, shown }. score is a raw score to
// set, or null to leave the rating alone; clearScore removes it. shown is the saved state the draft was made against, kept
// so that a save after the tracker changed elsewhere comes back stale instead of overwriting (D4). Drafts for one row are
// a map of provider to draft; an edit that returns an entry to its saved state removes its draft.

const rewatching = targets => targets.find(target => target.entry?.rewatching);

const effectiveScore = (target, draft) => draft?.clearScore ? null : draft?.score ?? target.entry?.score ?? null;

function startDraft(target, drafts) {
    const saved = savedState(target);
    return drafts[target.provider] ? { ...drafts[target.provider] } : { status: saved.status, progress: saved.progress, score: null, clearScore: false, shown: saved };
}

export function isDirty(target, draft) {
    if (!draft)
        return false;
    const saved = savedState(target);
    return draft.status !== saved.status || draft.progress !== saved.progress || (draft.clearScore && saved.score !== null)
        || (draft.score !== null && draft.score !== saved.score);
}

/** Applies change to every target's draft. Nothing changes while an entry is being rewatched (D11). */
function edit(targets, drafts, change) {
    if (rewatching(targets))
        return drafts;
    const next = { ...drafts };
    for (const target of editableTargets(targets)) {
        const draft = change(startDraft(target, drafts), target);
        if (!draft)
            continue;
        if (isDirty(target, draft))
            next[target.provider] = draft;
        else
            delete next[target.provider];
    }
    return next;
}

const clamp = (value, total) => Math.max(0, total > 0 ? Math.min(total, value) : value);

export const setProgress = (targets, drafts, value) => edit(targets, drafts, (draft, target) => {
    draft.progress = clamp(Math.round(value), target.entry.totalEpisodes);
    if (draft.progress > 0 && (draft.status === null || draft.status === 'PlanToWatch'))
        draft.status = 'Watching';
    return draft;
});

export const setStatus = (targets, drafts, status) => status === 'Mixed' || !statusOrder.includes(status) ? drafts : edit(targets, drafts, (draft, target) => {
    draft.status = status;
    if (status === 'Completed' && target.entry.totalEpisodes > 0)
        draft.progress = target.entry.totalEpisodes;
    if (status === 'PlanToWatch')
        draft.progress = 0;
    return draft;
});

export const markCompleted = (targets, drafts) => setStatus(targets, drafts, 'Completed');

/**
 * Sets the rating from a shown value in each tracker's own format; null clears it. With toggle (stars and faces), choosing
 * the current value clears it. An entry not on the list joins it as Plan to watch.
 */
export const setScore = (targets, drafts, display, { toggle = false } = {}) => edit(targets, drafts, (draft, target) => {
    const format = target.account.supportsScore ? target.account.scoreFormat : null;
    if (!format)
        return null;
    const current = toDisplay(effectiveScore(target, draft), format);
    const clear = display === null || (toggle && display === current);
    const raw = clear ? null : toRaw(display, format);
    if (!clear && raw === null)
        return null;
    draft.score = raw;
    draft.clearScore = clear && target.entry.score !== null;
    if (draft.status === null)
        draft.status = 'PlanToWatch';
    return draft;
});

// --- Editor model ------------------------------------------------------------------------------------------

const same = values => values.every(value => value === values[0]);
const listed = items => items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;

function ratingField(targets, shown) {
    const formats = targets.map(target => target.account.supportsScore ? target.account.scoreFormat : null);
    if (!formats.length || formats.some(format => !format) || !formats.every(format => sameScale(format, formats[0])))
        return null;
    const used = formats.map(format => scales[format]);
    const kind = formats[0] === 'Point5' ? 'stars' : formats[0] === 'Point3' ? 'faces' : 'number';
    const scores = shown.map(item => item.score);
    return {
        kind, max: used[0].max, step: Math.max(...used.map(scale => scale.step)), min: Math.max(...used.map(scale => scale.min)),
        value: same(scores) ? toDisplay(scores[0], formats[0]) : 'Mixed'
    };
}

/**
 * Everything the expanded pill shows for one tracker, or for All trackers (all: true): the fields, the notes and whether
 * anything is unsaved. A field is null when it is not shown: progress when the trackers' totals differ, rating when their
 * scales differ or a tracker has no ratings.
 */
export function editorModel(targets, drafts, { all }) {
    const providers = targets.map(target => target.provider);
    const shown = targets.map(target => {
        const draft = drafts[target.provider] ?? savedState(target);
        return { status: draft.status, progress: draft.progress, score: effectiveScore(target, drafts[target.provider]) };
    });
    const totals = targets.map(target => target.entry.totalEpisodes), total = same(totals) ? totals[0] : null;
    const statusValue = same(shown.map(item => item.status)) ? shown[0]?.status ?? null : 'Mixed';
    const progress = total === null ? null : { value: Math.max(0, ...shown.map(item => item.progress)), max: total > 0 ? total : null };
    const rating = ratingField(targets, shown);
    const label = status => all ? sharedStatusLabel(status, providers) : statusLabel(status, providers[0]);
    const statuses = (statusValue === 'Mixed' ? [['Mixed', 'Mixed']] : []).concat(statusOrder.map(status => [status, label(status)]));
    const diffs = [];
    if (all && targets.length > 1) {
        const each = text => targets.map((target, index) => `${providerName(target.provider)} - ${text(target, shown[index])}`).join(', ');
        if (statusValue === 'Mixed')
            diffs.push(['status', each((target, item) => statusLabel(item.status, target.provider))]);
        if (!same(shown.map(item => item.progress)))
            diffs.push(['watch progress', each((target, item) => `${item.progress} of ${target.entry.totalEpisodes || '?'}`)]);
        if (rating?.value === 'Mixed')
            diffs.push(['rating', each((target, item) => scoreText(item.score, target.account.scoreFormat))]);
    }
    const lowering = targets.filter(target => isDirty(target, drafts[target.provider]) && drafts[target.provider].progress < savedState(target).progress);
    const rewatched = rewatching(targets);
    return {
        fields: { status: { value: statusValue }, progress, rating },
        statuses, total,
        jellyfin: Math.max(0, ...targets.map(target => target.entry.jellyfinProgress ?? 0)),
        readOnly: Boolean(rewatched),
        readOnlyReason: rewatched ? `This entry is being rewatched. Change it on ${providerName(rewatched.provider)} for now` : '',
        mixedNote: diffs.length === 0 ? '' : diffs.length === 1 ? `The trackers differ in ${diffs[0][0]}: ${diffs[0][1]}`
            : `The trackers differ in ${listed(diffs.map(diff => diff[0]))}`,
        lowerNote: lowering.map(target => `Lowers ${providerName(target.provider)} from ${savedState(target).progress} to ${drafts[target.provider].progress}`).join('. '),
        suggestComplete: Boolean(progress?.max) && progress.value === progress.max && statusValue !== 'Completed',
        dirty: targets.some(target => isDirty(target, drafts[target.provider]))
    };
}

// --- Sync buttons ------------------------------------------------------------------------------------------

const jellyfinOf = target => target.entry.jellyfinProgress ?? 0;

/** The two sync buttons: enabled, or disabled with the reason a tooltip gives (spec, Overview tab, item 4). */
export function syncButtons(targets, drafts, { all }) {
    const name = all ? null : providerName(targets[0]?.provider);
    const saved = targets.map(target => savedState(target).progress);
    const dirty = targets.some(target => isDirty(target, drafts[target.provider]));
    const behind = targets.some((target, index) => saved[index] < jellyfinOf(target));
    const ahead = targets.some((target, index) => saved[index] > jellyfinOf(target));
    const agree = targets.length > 0 && same(saved);
    const down = !dirty && agree && targets.every((target, index) => saved[index] > jellyfinOf(target));
    const upName = all ? 'Sync to all trackers' : `Sync to ${name}`;
    return {
        up: {
            label: upName, enabled: dirty || behind,
            reason: dirty || behind ? '' : ahead ? `${all ? 'Your trackers are' : `${name} is`} ahead of Jellyfin. Use Sync to Jellyfin` : 'Up to date'
        },
        down: {
            label: 'Sync to Jellyfin', enabled: down,
            reason: down ? '' : dirty ? `Sync your changes to ${all ? 'your trackers' : name} first`
                : !agree && targets.length > 0 ? 'Trackers differ. Open one tracker to sync from it'
                    : targets.every((target, index) => saved[index] === jellyfinOf(target)) ? 'Up to date' : `Jellyfin is ahead. Use ${upName}`
        }
    };
}

// --- Requests ----------------------------------------------------------------------------------------------

function editFrom(target, draft) {
    return {
        provider: target.provider, entryId: target.entry.id, alternativeId: target.entry.alternativeId ?? null, shown: draft.shown,
        status: draft.status ?? 'PlanToWatch', progress: draft.progress, score: draft.clearScore ? null : draft.score, clearScore: draft.clearScore
    };
}

/**
 * What Sync to <tracker> sends (6.4 plan, decision 8): pending edits as one edit request, then a series sync for each clean
 * tracker Jellyfin is ahead of. A movie cannot take a series sync, so a clean movie entry is raised by the edit instead.
 */
export function planSyncUp(targets, drafts, { isMovie, itemId }) {
    const edits = [], syncs = [];
    for (const target of editableTargets(targets)) {
        const draft = drafts[target.provider], saved = savedState(target);
        if (isDirty(target, draft))
            edits.push(editFrom(target, draft));
        else if (saved.progress < jellyfinOf(target)) {
            if (isMovie)
                edits.push(editFrom(target, { status: 'Completed', progress: jellyfinOf(target), score: null, clearScore: false, shown: saved }));
            else
                syncs.push(target.provider);
        }
    }
    return { edit: edits.length ? { itemId, edits } : null, syncs };
}

/** The tracker Sync to Jellyfin reads from; the targets agree whenever it is enabled, so the first will do. */
export function planSyncDown(targets, drafts) {
    const ready = editableTargets(targets);
    return syncButtons(ready, drafts, { all: ready.length > 1 }).down.enabled ? ready[0].provider : null;
}

// --- Save results ------------------------------------------------------------------------------------------

const failureHeadings = {
    transient: name => `Couldn't reach ${name}`,
    'rate-limited': name => `${name} is busy. Try again in a minute`,
    unauthorized: name => `${name} needs you to sign in again`,
    rejected: name => `${name} refused the change`
};
const describe = (state, target) => `${statusLabel(state.status, target.provider)}, ${state.progress ?? 0} of ${target.entry.totalEpisodes || '?'}`;

/**
 * Applies an edit's per-tracker results (EditResultContract) to the row's drafts. A saved or unconfirmed entry loses its draft;
 * a stale or failed one keeps it, with a card that says what happened and offers the next step (canvas States board).
 */
export function applyEditResults(drafts, results, targetsByProvider) {
    const next = { ...drafts }, cards = [];
    for (const item of results) {
        const target = targetsByProvider[item.provider], draft = drafts[item.provider];
        if (!target)
            continue;
        const name = providerName(item.provider), base = { provider: item.provider, current: item.current ?? null };
        switch (item.result) {
            case 'Saved':
                delete next[item.provider];
                break;
            case 'Unconfirmed':
                delete next[item.provider];
                cards.push({ ...base, kind: 'unconfirmed', heading: `${name} didn't confirm the change`, body: `Check it on ${name}.`, actions: [] });
                break;
            case 'Stale': {
                if (!draft || !item.current)
                    break;
                const lowers = draft.progress < (item.current.progress ?? 0);
                cards.push({
                    ...base, kind: 'stale', heading: `${name} changed while this was open`,
                    body: `It now shows ${describe(item.current, target)}. ${lowers ? `Saving would set it back to ${draft.progress}.` : `Saving would change it to ${describe(draft, target)}.`}`,
                    actions: [{ id: 'load', label: `Load ${name}'s` }, { id: 'force', label: draft.progress !== (item.current.progress ?? 0) ? `Save ${draft.progress} anyway` : 'Save anyway' }]
                });
                break;
            }
            case 'Failed':
            case 'NotFound': {
                if (!draft)
                    break;
                const gone = item.result === 'NotFound';
                cards.push({
                    ...base, kind: 'failed',
                    heading: gone ? `${name} no longer has this entry` : (failureHeadings[item.failure] ?? failureHeadings.rejected)(name),
                    body: `${describe(draft, target)} isn't saved yet. ${name} still shows ${describe(savedState(target), target)}.`,
                    actions: gone ? [{ id: 'undo', label: 'Undo' }] : [{ id: 'retry', label: 'Try again' }, { id: 'undo', label: 'Undo' }]
                });
                break;
            }
            default:
                break;
        }
    }
    return { drafts: next, cards };
}

/** "Save anyway": the same draft, made against what the tracker now shows, so the next save passes the stale check. */
export function forceDraft(drafts, provider, current) {
    return drafts[provider] ? { ...drafts, [provider]: { ...drafts[provider], shown: { status: current.status ?? null, progress: current.progress ?? 0, score: current.score ?? null } } } : drafts;
}

// --- Saved drafts (D12) ------------------------------------------------------------------------------------

/** Drafts for one user and series in sessionStorage, by row key. Storage that throws or holds garbage reads as empty. */
export function draftStore(storage, userId, seriesId) {
    const key = `anidoki:drafts:${userId}:${seriesId}`;
    const prune = drafts => Object.fromEntries(Object.entries(drafts ?? {}).filter(([, row]) => row && Object.keys(row).length));
    return {
        load() {
            try {
                const parsed = JSON.parse(storage?.getItem(key) ?? '{}');
                return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? prune(parsed) : {};
            } catch {
                return {};
            }
        },
        save(drafts) {
            try {
                const kept = prune(drafts);
                if (Object.keys(kept).length)
                    storage?.setItem(key, JSON.stringify(kept));
                else
                    storage?.removeItem(key);
            } catch {
                // Private windows and full storage: the drafts live for this page only.
            }
        },
        clear() {
            try {
                storage?.removeItem(key);
            } catch {
                // As above.
            }
        }
    };
}

export const dirtyRows = drafts => new Set(Object.entries(drafts).filter(([, row]) => row && Object.keys(row).length).map(([key]) => key));

export const anyDirty = drafts => dirtyRows(drafts).size > 0;
