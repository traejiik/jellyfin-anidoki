// The match editor's state, with no DOM: the draft as typed, the saved match it started from, and the checks that
// can run before a save. Shared by the admin manager and, from PR 14, the user page. Every function returns a new
// state; nothing here is mutated in place.

// The same bound as MatchValidator.MaxEpisodeNumber.
export const maxEpisode = 1_000_000;

let serial = 0;
const key = () => `row-${++serial}`;
const text = value => value == null ? '' : String(value);

// An entry as the API sends it. Equal entries share a key; the title is left out, since it is only a label.
export const entryKey = entry => entry ? `${entry.id}|${entry.alternativeId ?? ''}|${JSON.stringify(entry.lookup ?? null)}` : '';

// Ranges and exceptions as typed. Numbers stay strings until they are checked, so an input never jumps under the cursor.
export function fromMapping(mapping) {
    return {
        ranges: (mapping?.ranges ?? []).map(range => ({ key: key(), sourceStart: text(range.sourceStart), sourceEnd: text(range.sourceEnd),
            target: range.target ?? null, targetStart: text(range.targetStart) })),
        exceptions: (mapping?.exceptions ?? []).map(exception => ({ key: key(), sourceEpisode: text(exception.sourceEpisode),
            kind: exception.kind === 'target' ? 'target' : 'ignore', target: exception.target ?? null, targetEpisode: text(exception.targetEpisode) }))
    };
}

// saved: the match the draft started from, or null for none. Its revision is what a save must expect.
export function createEditor(saved = null) {
    const mapping = saved?.mapping ?? { ranges: [], exceptions: [] };
    return { saved: { revision: saved?.revision ?? 0, mapping, note: saved?.note ?? '' }, draft: { ...fromMapping(mapping), note: saved?.note ?? '' }, conflict: null };
}

const withDraft = (state, change) => ({ ...state, draft: { ...state.draft, ...change } });

export const addRange = state => {
    // A new range continues from the last one, which is what a split season usually wants.
    const last = state.draft.ranges.at(-1);
    const end = last && /^\d+$/.test(last.sourceEnd) ? Number(last.sourceEnd) : null;
    const row = { key: key(), sourceStart: end == null ? (last ? '' : '1') : String(end + 1), sourceEnd: '', target: null, targetStart: '1' };
    return withDraft(state, { ranges: [...state.draft.ranges, row] });
};
export const addException = state =>
    withDraft(state, { exceptions: [...state.draft.exceptions, { key: key(), sourceEpisode: '', kind: 'ignore', target: null, targetEpisode: '' }] });
export const removeRow = (state, rowKey) => withDraft(state, {
    ranges: state.draft.ranges.filter(row => row.key !== rowKey), exceptions: state.draft.exceptions.filter(row => row.key !== rowKey)
});
// field is one of the row's own properties; anything else is ignored.
export function updateRow(state, rowKey, field, value) {
    const update = rows => rows.map(row => row.key === rowKey && field in row && field !== 'key' ? { ...row, [field]: value } : row);
    return withDraft(state, { ranges: update(state.draft.ranges), exceptions: update(state.draft.exceptions) });
}
export const setNote = (state, note) => withDraft(state, { note });
// Replaces the draft, keeping the saved match and its revision: "Save automatic as match" starts from here.
export const replaceDraft = (state, mapping) => ({ ...state, draft: { ...fromMapping(mapping), note: state.draft.note } });
export const discard = state => ({ ...state, draft: { ...fromMapping(state.saved.mapping), note: state.saved.note }, conflict: null });

// A save answered 409: someone else saved first. The draft stays until the user reloads.
export const markConflict = (state, currentRevision) => ({ ...state, conflict: { currentRevision } });
export const markSaved = (state, revision) =>
    ({ saved: { revision, mapping: toMapping(state.draft), note: state.draft.note }, draft: state.draft, conflict: null });

const number = value => /^\d+$/.test(String(value).trim()) ? Number(String(value).trim()) : NaN;

// The draft as the API takes it. Blank and unparsable numbers become NaN here and are reported by check().
export function toMapping(draft) {
    return {
        ranges: draft.ranges.map(row => ({ sourceStart: number(row.sourceStart), sourceEnd: String(row.sourceEnd).trim() === '' ? null : number(row.sourceEnd),
            target: row.target, targetStart: number(row.targetStart) })),
        exceptions: draft.exceptions.map(row => row.kind === 'target'
            ? { sourceEpisode: number(row.sourceEpisode), kind: 'target', target: row.target, targetEpisode: number(row.targetEpisode) }
            : { sourceEpisode: number(row.sourceEpisode), kind: 'ignore' })
    };
}

const comparable = mapping => JSON.stringify({
    ranges: mapping.ranges.map(range => [range.sourceStart, range.sourceEnd ?? null, entryKey(range.target), range.targetStart]),
    exceptions: [...mapping.exceptions].sort((a, b) => a.sourceEpisode - b.sourceEpisode)
        .map(exception => [exception.sourceEpisode, exception.kind, entryKey(exception.target), exception.targetEpisode ?? null])
});
export const isDirty = state => state.draft.note !== state.saved.note || comparable(toMapping(state.draft)) !== comparable(state.saved.mapping);

// What would stop the server from even reading the draft: a blank number or a row with no entry. Each issue names its row.
function checkInput(draft) {
    const issues = [];
    draft.ranges.forEach((row, index) => {
        const name = `Range ${index + 1}`;
        if (Number.isNaN(number(row.sourceStart))) issues.push({ rule: 'input', row: row.key, message: `${name}: enter the first episode.` });
        if (String(row.sourceEnd).trim() !== '' && Number.isNaN(number(row.sourceEnd))) issues.push({ rule: 'input', row: row.key, message: `${name}: the last episode must be a number, or blank to follow an airing entry.` });
        if (Number.isNaN(number(row.targetStart))) issues.push({ rule: 'input', row: row.key, message: `${name}: enter the entry episode it starts at.` });
        if (!row.target) issues.push({ rule: 'input', row: row.key, message: `${name}: choose an entry.` });
    });
    draft.exceptions.forEach(row => {
        const name = row.sourceEpisode ? `Exception for episode ${row.sourceEpisode}` : 'An exception';
        if (Number.isNaN(number(row.sourceEpisode))) issues.push({ rule: 'input', row: row.key, message: `${name}: enter the episode.` });
        if (row.kind === 'target' && !row.target) issues.push({ rule: 'input', row: row.key, message: `${name}: choose an entry.` });
        if (row.kind === 'target' && Number.isNaN(number(row.targetEpisode))) issues.push({ rule: 'input', row: row.key, message: `${name}: enter the entry's episode.` });
    });
    return issues;
}

const contains = (range, episode) => episode >= range.sourceStart && (range.sourceEnd == null || episode <= range.sourceEnd);
const targetEnd = range => range.sourceEnd == null ? null : range.targetStart + (range.sourceEnd - range.sourceStart);

// V2, V3 and V5, worded and ordered as MatchValidator words them, so the page never refuses what the server would
// accept. The rest (V1, V4, V6–V8 and the warnings) needs entry totals or tracker reads, and stays with the server.
export function check(draft) {
    const input = checkInput(draft);
    if (input.length) return input;
    const { ranges, exceptions } = toMapping(draft);
    const rowOf = range => draft.ranges[ranges.indexOf(range)].key;
    const errors = [];
    ranges.forEach((range, index) => {
        if (range.sourceStart < 0 || range.sourceEnd != null && range.sourceEnd < range.sourceStart)
            errors.push({ rule: 'V2', row: rowOf(range), message: `Range ${index + 1} must run forwards from episode 0 or later.` });
        if (range.sourceEnd == null && index !== ranges.length - 1)
            errors.push({ rule: 'V2', row: rowOf(range), message: `Range ${index + 1} is open, but only the last range can be.` });
        if ([range.sourceStart, range.sourceEnd ?? 0, range.targetStart].some(value => value > maxEpisode))
            errors.push({ rule: 'V2', row: rowOf(range), message: `Range ${index + 1} has an episode number above ${maxEpisode}.` });
    });
    exceptions.forEach((exception, index) => {
        if (exception.sourceEpisode > maxEpisode)
            errors.push({ rule: 'V2', row: draft.exceptions[index].key, message: `Exception episode ${exception.sourceEpisode} is out of range.` });
        if (exception.kind === 'target' && exception.targetEpisode > maxEpisode)
            errors.push({ rule: 'V2', row: draft.exceptions[index].key, message: `The exception for episode ${exception.sourceEpisode} has an episode number above ${maxEpisode}.` });
    });
    // The server stops at shape errors too: everything after this relies on sorted, bounded ranges.
    if (errors.length) return errors;

    const sorted = [...ranges].sort((a, b) => a.sourceStart - b.sourceStart);
    sorted.slice(1).forEach((second, index) => {
        const first = sorted[index];
        if (first.sourceEnd == null || second.sourceStart <= first.sourceEnd)
            errors.push({ rule: 'V3', row: rowOf(second), message: `The ranges starting at episodes ${first.sourceStart} and ${second.sourceStart} overlap.` });
    });
    const seen = new Map();
    exceptions.forEach((exception, index) => {
        if (seen.get(exception.sourceEpisode) === 1)
            errors.push({ rule: 'V3', row: draft.exceptions[index].key, message: `Episode ${exception.sourceEpisode} has more than one exception.` });
        seen.set(exception.sourceEpisode, (seen.get(exception.sourceEpisode) ?? 0) + 1);
    });

    errors.push(...ambiguity(ranges, exceptions, draft, rowOf));
    return errors;
}

// V5: no two source episodes may land on one entry episode. An episode with an exception has left its range.
function ambiguity(ranges, exceptions, draft, rowOf) {
    const issues = [];
    const add = (target, episode, row) => issues.push({ rule: 'V5', row, message: `More than one episode goes to episode ${episode} of "${target.title}".` });
    const excepted = new Set(exceptions.map(exception => exception.sourceEpisode));
    const targets = exceptions.map((exception, index) => ({ ...exception, row: draft.exceptions[index].key })).filter(exception => exception.kind === 'target');
    const shared = new Map();
    for (const exception of targets) {
        const at = `${entryKey(exception.target)}#${exception.targetEpisode}`;
        if (shared.get(at) === 1) add(exception.target, exception.targetEpisode, exception.row);
        shared.set(at, (shared.get(at) ?? 0) + 1);
    }
    for (const exception of targets) {
        for (const range of ranges.filter(range => entryKey(range.target) === entryKey(exception.target))) {
            const source = range.sourceStart + (exception.targetEpisode - range.targetStart);
            if (contains(range, source) && !excepted.has(source)) add(exception.target, exception.targetEpisode, exception.row);
        }
    }
    for (let outer = 0; outer < ranges.length; outer++) {
        for (let inner = outer + 1; inner < ranges.length; inner++) {
            const first = ranges[outer], second = ranges[inner];
            if (entryKey(first.target) !== entryKey(second.target)) continue;
            const episode = sharedTarget(first, second, excepted);
            if (episode != null) add(first.target, episode, rowOf(second));
        }
    }
    return issues;
}

// As MatchValidator.SharedTarget. Both ranges move one episode per episode, so if they reach a target from the same
// source once they always do: that is overlapping ranges, which V3 reports, and the server's loop finds nothing
// either. Stopping there keeps this loop as short as the server's otherwise is: it only continues past excepted episodes.
function sharedTarget(first, second, excepted) {
    const low = Math.max(first.targetStart, second.targetStart);
    const high = Math.min(targetEnd(first) ?? Number.MAX_SAFE_INTEGER, targetEnd(second) ?? Number.MAX_SAFE_INTEGER);
    for (let episode = low; episode <= high; episode++) {
        const fromFirst = first.sourceStart + (episode - first.targetStart);
        const fromSecond = second.sourceStart + (episode - second.targetStart);
        if (fromFirst === fromSecond) return null;
        if (!excepted.has(fromFirst) && !excepted.has(fromSecond)) return episode;
    }
    return null;
}

// The request body for a save.
export const saveRequest = state => ({ expectedRevision: state.saved.revision, ...toMapping(state.draft), note: state.draft.note.trim() || null });

// The source list: search, one filter, and a 1-based page.
// The source lists' chips: the admin's sees every user's problems, a user's their own. Needs a match and Needs review
// come from recent syncs (MatchProblems.cs).
export const filters = [{ key: '', label: 'All' }, { key: 'needs-match', label: 'Needs a match' }, { key: 'needs-review', label: 'Needs review' },
    { key: 'matched', label: 'Saved' }, { key: 'corrections', label: 'Corrections' }];
export const userFilters = [{ key: '', label: 'All' }, { key: 'needs-match', label: 'Needs a match' }, { key: 'needs-review', label: 'Needs review' },
    { key: 'corrections', label: 'Your corrections' }];
export const createList = () => ({ search: '', filter: '', page: 1 });
// A new search or filter starts again from the first page.
export const setSearch = (list, search) => ({ ...list, search, page: 1 });
export const setFilter = (list, filter, options = filters) => options.some(item => item.key === filter) ? { ...list, filter, page: 1 } : list;
export const pageCount = result => Math.max(1, Math.ceil((result?.total ?? 0) / (result?.pageSize || 50)));
export const setPage = (list, page, count) => ({ ...list, page: Math.min(Math.max(1, page), Math.max(1, count)) });
export function listPath(base, list) {
    const query = new URLSearchParams();
    if (list.search.trim()) query.set('search', list.search.trim());
    if (list.filter) query.set('filter', list.filter);
    if (list.page > 1) query.set('page', String(list.page));
    const text = query.toString();
    return `${base}/sources${text ? `?${text}` : ''}`;
}
// Source keys hold colons; each segment is encoded on its own.
export const routePath = (base, ...segments) => [base, ...segments.map(segment => encodeURIComponent(segment))].join('/');

// One line for a provider in the source list. Providers with nothing saved are not sent, and resolve automatically.
export function stateLabel(state) {
    const parts = [];
    if (state.sharedRevision != null) parts.push(`Shared r${state.sharedRevision}`);
    if (state.personalRevision != null) parts.push('Personal');
    if (state.corrections) parts.push(`${state.corrections} ${state.corrections === 1 ? 'correction' : 'corrections'}`);
    return parts.join(' · ') || 'Automatic';
}

// The badge on a provider tab: what sync uses for a user with no correction of their own.
export const resolutionBadge = shared => shared ? `Shared r${shared.revision}` : 'Automatic';

// The badge on a user's tracker tab: what their syncs use.
export const effectiveBadge = entry => entry.personal ? `Personal r${entry.personal.revision}` : resolutionBadge(entry.shared);

const automaticReasons = {
    'offset-not-representable': 'it uses an episode offset a match cannot hold',
    'no-ids': 'the season has no IDs to find it by',
    'no-title-match': 'no entry’s title matches',
    'sequel-fetch-failed': 'a sequel could not be read',
    'no-cour-covers-episode': 'no part of the series covers this episode'
};
// Why automatic matching gives no mapping to save. own: the reader is the user whose link it would use.
export const automaticReason = (reason, { own = false } = {}) => reason === 'no-linked-account'
    ? (own ? 'this tracker is not linked' : 'no user has linked this tracker')
    : automaticReasons[reason] ?? reason ?? 'it found no entry';

// A save the API refused, from its status and body. message is null when the status has no wording of its own.
export function saveFailure(status, body) {
    if (status === 409) return { conflict: body?.currentRevision ?? 0, message: 'Not saved.' };
    if (status === 422) return { conflict: null, message: ['Not saved.', ...(body?.errors ?? []).map(issue => issue.message)].join(' ') };
    return { conflict: null, message: null };
}
export const conflictMessage = (revision, what = 'this match') => (revision
    ? `Someone saved revision ${revision} of ${what} while you were editing.`
    : `Someone deleted ${what} while you were editing.`) + ' Your draft is still here; reloading replaces it with what is saved now.';

// One entry of a preview's progress. whose: "their" for an admin looking at a user, "your" on the user's own page.
export const progressLine = (entry, whose = 'their') =>
    `${entry.target.title}: episode ${entry.progress} watched${entry.listStatus ? ` (list: ${entry.listStatus}, ${entry.listProgress ?? 0})` : ` (not on ${whose} list)`}`;

const reasons = {
    'not-in-match': 'Not in this match',
    'match-target-unreadable': 'Entry could not be read',
    'past-entry-end': 'Past the entry’s last episode'
};
// One preview row in words. A row that would not sync still names where the match sends it.
export function describeRow(row) {
    const target = row.target ? `${row.target.title} · episode ${row.targetEpisode}` : '';
    if (row.outcome === 'mapped') return { text: target, tone: '' };
    if (row.outcome === 'ignored') return { text: 'Ignored', tone: 'muted' };
    return { text: [reasons[row.outcome] ?? row.outcome, target].filter(Boolean).join(': '), tone: 'error' };
}

// One line under an orphan: what was saved, and when it was last changed.
export function orphanSummary(orphan, providerName = name => name) {
    const saved = (orphan.providers ?? []).map(entry => `${providerName(entry.provider)}: ${stateLabel(entry)}`).join(', ') || 'Only history is left';
    const when = orphan.updatedUtc ? new Date(orphan.updatedUtc).toLocaleDateString() : '';
    return [`${orphan.kind === 'Movie' ? 'Movie' : 'Season'}`, saved, when && `last saved ${when}`].filter(Boolean).join(' · ');
}
