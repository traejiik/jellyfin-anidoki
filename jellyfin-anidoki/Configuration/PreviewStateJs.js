// The manual-sync preview's wording, filters, paging and grouping. Pure: no DOM and no requests, so Node tests it directly.
// Outbound rows carry the tracker's own records (PascalCase: List.Progress); inbound rows carry camelCase ones.
export const pageSize = 200;
export const filters = [
    { key: 'changes', label: 'Changes', kinds: ['Change'] },
    { key: 'reductions', label: 'Lowers progress', kinds: ['Reduction'] },
    { key: 'problems', label: 'Problems', kinds: ['Unresolved', 'Failed'] },
    { key: 'all', label: 'Everything', kinds: [] }
];
const filterOf = key => filters.find(filter => filter.key === key) ?? filters[filters.length - 1];
const everything = counts => Object.values(counts ?? {}).reduce((sum, count) => sum + count, 0);

export function filterCount(key, counts) {
    const { kinds } = filterOf(key);
    return kinds.length ? kinds.reduce((sum, kind) => sum + (counts?.[kind] ?? 0), 0) : everything(counts);
}

// Opens on the first view that has something in it.
export const defaultFilter = counts => filters.find(filter => filter.kinds.length && filterCount(filter.key, counts) > 0)?.key ?? 'all';

export function rowsPath(operationId, key, offset) {
    const query = new URLSearchParams({ offset: String(offset), limit: String(pageSize) });
    filterOf(key).kinds.forEach(kind => query.append('kind', kind));
    return `AniDoki/preview/${operationId}/rows?${query}`;
}

// One line under the table: a lone page only counts its rows; past one page, which rows and of how many.
export const paged = total => total > pageSize;
export function pageLabel(offset, shown, total) {
    if (!total) return '';
    if (!paged(total)) return `${total} ${total === 1 ? 'row' : 'rows'}`;
    return `Rows ${offset + 1}–${offset + shown} of ${total}`;
}

const emptyLabels = {
    changes: 'Nothing to change: the trackers already match.', reductions: 'No tracker is ahead of Jellyfin.',
    problems: 'No problems found.', all: 'Nothing was previewed.'
};
export const emptyLabel = key => emptyLabels[filterOf(key).key];

// Rows arrive ordered by season, so consecutive rows of one season share a heading.
export function groupRows(rows) {
    const groups = [];
    for (const row of rows) {
        const key = `${row.sourceKey}\u0000${row.sourceTitle}`;
        if (groups.at(-1)?.key !== key)
            groups.push({ key, title: row.sourceKey ? row.sourceTitle : `${row.sourceTitle} · no matching Jellyfin season`, rows: [] });
        groups.at(-1).rows.push(row);
    }
    return groups.map(group => ({ ...group, summary: groupSummary(group.rows), collapsed: !needsAttention(group.rows) }));
}

const plural = (count, one, many) => `${count} ${count === 1 ? one : many}`;
const needsAttention = rows => rows.some(row => ['Change', 'Reduction', 'Unresolved', 'Failed'].includes(row.kind));
// A season's heading says what is in it, which matters most while it is collapsed.
export function groupSummary(rows) {
    const count = (...kinds) => rows.filter(row => kinds.includes(row.kind)).length;
    const parts = [
        [count('Change'), 'change', 'changes'], [count('Reduction'), 'would lower progress', 'would lower progress'],
        [count('Unresolved', 'Failed'), 'problem', 'problems']
    ].filter(([n]) => n).map(([n, one, many]) => plural(n, one, many));
    if (parts.length) return parts.join(' · ');
    return rows.every(row => row.kind === 'Skipped') ? 'Skipped by your rules' : 'Up to date';
}

// Why a row proposes what it does, in a short plain sentence.
const reasons = {
    progress: 'You have watched more in Jellyfin', status: 'The status should match what you have watched',
    'rewatch-count': 'Finishing a rewatch adds one to the rewatch count', reduction: 'The tracker has more watched than Jellyfin',
    'already-watched': 'Already watched on the tracker', 'manual-sync-rewatch': 'Manual sync does not update rewatches',
    'completed-on-tracker': 'Already completed on the tracker', 'dropped-on-tracker': 'Marked as dropped on the tracker',
    'plan-to-watch-only': 'Your rules only update entries on Plan to watch', 'rewatch-disabled': 'Already completed, and rewatch tracking is off',
    'rewatch-unsupported': 'This tracker does not record rewatches', 'match-ignored': 'A saved match leaves this episode out',
    resolve: 'Not found on the tracker', unauthorized: 'The tracker did not accept the sign-in; link the account again',
    'rate-limited': 'The tracker is limiting requests; try again later', transient: 'The tracker did not respond; try again later',
    rejected: 'The tracker refused the update',
    'mark-played': 'Watched on the tracker, not yet played in Jellyfin', 'already-played': 'Already played in Jellyfin',
    ignored: 'The season has AniDōki’s ignore tag', 'no-anidb-id': 'The entry has no AniDB ID to match it with',
    'no-season': 'The anime list does not link this entry to a season'
};
// The planner also answers "status" when nothing needs changing, which needs its own words.
const quietReasons = { status: 'The status already matches' };
export const reasonLabel = (reason, kind) => (kind === 'NoChange' ? quietReasons[reason] : undefined) ?? reasons[reason] ?? reason;

// Why a season could not be matched, from the resolver's note.
const notes = {
    'no-ids': 'The series has no tracker or AniDB IDs to search with', 'not-found': 'The tracker has no matching entry',
    'no-title-match': 'No entry’s title matched the series', 'no-special-match': 'No special or OVA on the tracker matched',
    'sequel-fetch-failed': 'The entry’s sequels could not be read from the tracker',
    'no-cour-covers-episode': 'None of the tracker’s parts of this series include this episode',
    'past-entry-end': 'This episode comes after the entry’s last episode',
    'offset-not-representable': 'This episode could not be placed on the tracker’s split entries',
    'not-in-match': 'The saved match does not cover this episode', 'match-target-unreadable': 'The entry in the saved match could not be read'
};
export const noteLabel = note => note ? notes[note] ?? note : '';

const statusNames = {
    Watching: 'Watching', Completed: 'Completed', OnHold: 'On hold', On_hold: 'On hold', Dropped: 'Dropped',
    PlanToWatch: 'Plan to watch', Plan_to_watch: 'Plan to watch', Rewatching: 'Rewatching'
};
const statusName = status => statusNames[status] ?? status;
// Annict records a status only, so a number would mislead.
const statusOnly = provider => provider === 'Annict';
const progress = (count, total) => `${count}${total ? ` of ${total}` : ''}`;
// What a proposal reads as when it proposes nothing.
const idle = { NoChange: 'No change', Skipped: 'Skipped', Unresolved: 'Not checked', Failed: 'Not checked' };

function markLabel(mark) {
    const count = mark.episodes?.length ?? 0;
    return count === 0 ? 'The season' : plural(count, 'episode', 'episodes');
}

// A cell that holds no value (—, Not on your list, No change, Not checked) reads muted, not bold.
const placeholders = new Set(['—', 'Not on your list', 'Nothing played', ...Object.values(idle)]);
const withQuiet = cells => ({ ...cells, nowQuiet: placeholders.has(cells.now), proposedQuiet: placeholders.has(cells.proposed) });

// One table row's cells, in plain words. Now and Proposed are a bold value with a muted note beneath (the status).
// A reduction colours what it proposes; a problem colours the reason. Tones are '', 'attention' or 'error'.
export function describeRow(row, providerName) {
    const tracker = providerName(row.provider);
    const entry = row.targetTitle || (row.targetId ? `Entry ${row.targetId}` : 'No matching entry');
    const why = reasonLabel(row.reason, row.kind);
    const proposedTone = row.kind === 'Reduction' ? 'attention' : '';
    const whyTone = row.kind === 'Failed' ? 'error' : row.kind === 'Unresolved' ? 'attention' : '';
    const base = { provider: row.provider, tracker, entry, why, proposedTone, whyTone };
    if (row.direction === 'FromTrackers') {
        const from = row.trackerEntry, mark = row.plannedMark;
        const marks = row.kind === 'Change' && mark;
        return withQuiet({
            ...base,
            now: !row.sourceKey ? '—' : row.watchedFingerprint ? `Played ${row.watchedFingerprint}` : 'Nothing played', nowNote: row.sourceKey ? 'In Jellyfin' : '',
            proposed: marks ? `→ ${markLabel(mark)}` : idle[row.kind] ?? '—',
            proposedNote: marks ? (mark.marksSeason && mark.episodes?.length ? 'Marked played, with the season' : 'Marked played') : '',
            whyNote: row.resolutionNote ? noteLabel(row.resolutionNote) : (from ? `${statusName(from.status)} · ${from.progress} watched on ${tracker}` : '')
        });
    }
    const against = row.plannedAgainst, list = against?.List, change = row.plannedChange, total = against?.TotalEpisodes;
    const value = (status, count) => statusOnly(row.provider) ? [statusName(status), ''] : [progress(count, total), statusName(status)];
    const [now, nowNote] = !against ? ['—', ''] : !list ? ['Not on your list', ''] : value(list.Status, list.Progress);
    let proposed = idle[row.kind] ?? '—', proposedNote = '';
    if (change && row.kind === 'Change') [proposed, proposedNote] = value(change.Status, change.Progress);
    if (change && row.kind === 'Reduction') [proposed, proposedNote] = [progress(change.Progress, total), 'Would lower progress'];
    if (change && ['Change', 'Reduction'].includes(row.kind)) proposed = `→ ${proposed}`;
    return withQuiet({ ...base, now, nowNote, proposed, proposedNote, whyNote: noteLabel(row.resolutionNote) });
}
