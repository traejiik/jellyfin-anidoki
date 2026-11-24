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
const seasonName = number => number === null || number === undefined ? 'Movie' : number === 0 ? 'Specials' : `Season ${number}`;
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
