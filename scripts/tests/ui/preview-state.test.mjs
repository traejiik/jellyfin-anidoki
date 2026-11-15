import test from 'node:test';
import assert from 'node:assert/strict';
import {
    defaultFilter, defaultSelection, describeRow, emptyLabel, episodeRange, filterCount, filters, filterSelection, filtersFor, groupRows, groupSummary, noteLabel,
    outOfDateLabel, pageLabel, paged, reasonLabel, resultLabel, rowsPath, selectAll, selectionSummary
} from '../../../jellyfin-anidoki/Configuration/PreviewStateJs.js';

const names = key => ({ AniList: 'AniList', Mal: 'MyAnimeList', Annict: 'Annict' })[key] ?? key;
const outbound = changes => ({
    direction: 'ToTrackers', provider: 'AniList', sourceKey: 'season:a:1', sourceTitle: 'Show · season 1', targetId: '500', targetTitle: 'Show',
    targetEpisode: 5, kind: 'Change', reason: 'progress', resolutionNote: null,
    plannedChange: { Progress: 5, Status: 'Watching' },
    plannedAgainst: { Entry: { Id: 500, Provider: 'AniList' }, TotalEpisodes: 12, List: { Status: 'Watching', Progress: 3, Rewatching: false } },
    ...changes
});
const inbound = changes => ({
    direction: 'FromTrackers', provider: 'AniList', sourceKey: 'season:a:1', sourceTitle: 'Show · season 1', targetId: '1001', targetTitle: 'Show',
    targetEpisode: 4, kind: 'Change', reason: 'mark-played', resolutionNote: null, watchedFingerprint: '1-2',
    plannedMark: { seasonItem: 's', episodes: ['e1', 'e2', 'e3', 'e4'], playedDate: '2026-01-02T00:00:00', marksSeason: true },
    trackerEntry: { provider: 'AniList', id: 1001, title: 'Show', status: 'Completed', progress: 4, finishDate: '2026-01-02' },
    ...changes
});

test('filters count their kinds, and Everything counts every row', () => {
    const counts = { Change: 3, Reduction: 1, Unresolved: 2, Failed: 1, NoChange: 5 };
    assert.deepEqual(filters.map(filter => filterCount(filter.key, counts)), [3, 1, 3, 12]);
    assert.equal(filterCount('changes', {}), 0);
});
test('a preview opens on the first filter with rows, else Everything', () => {
    assert.equal(defaultFilter({ Change: 1, Failed: 1 }), 'changes');
    assert.equal(defaultFilter({ Reduction: 2, NoChange: 4 }), 'reductions');
    assert.equal(defaultFilter({ Failed: 1 }), 'problems');
    assert.equal(defaultFilter({ NoChange: 4 }), 'all');
    assert.equal(defaultFilter({}), 'all');
});
test('the rows path repeats kind for Problems and pages by 200', () => {
    assert.equal(rowsPath('op', 'problems', 200), 'AniDoki/preview/op/rows?offset=200&limit=200&kind=Unresolved&kind=Failed');
    assert.equal(rowsPath('op', 'all', 0), 'AniDoki/preview/op/rows?offset=0&limit=200');
    assert.equal(rowsPath('op', 'unknown', 0), 'AniDoki/preview/op/rows?offset=0&limit=200');
});
test('one page only counts its rows; more pages say which rows', () => {
    assert.equal(paged(200), false);
    assert.equal(paged(201), true);
    assert.equal(pageLabel(0, 4, 4), '4 rows');
    assert.equal(pageLabel(0, 1, 1), '1 row');
    assert.equal(pageLabel(0, 0, 0), '');
    assert.equal(pageLabel(200, 43, 243), 'Rows 201–243 of 243');
});
test('empty views read plainly', () => {
    assert.equal(emptyLabel('reductions'), 'No tracker is ahead of Jellyfin.');
    assert.equal(emptyLabel('nonsense'), 'Nothing was previewed.');
});
test('consecutive rows of one season share a heading; rows with no season say so', () => {
    const groups = groupRows([outbound(), outbound({ provider: 'Mal' }), outbound({ sourceKey: 'season:a:2', sourceTitle: 'Show · season 2' }),
        inbound({ sourceKey: '', sourceTitle: 'Lost Show', kind: 'Unresolved' })]);
    assert.deepEqual(groups.map(group => [group.title, group.rows.length]),
        [['Show · season 1', 2], ['Show · season 2', 1], ['Lost Show · no matching Jellyfin season', 1]]);
});
test('a season heading sums up its rows, and up-to-date seasons start collapsed', () => {
    assert.equal(groupSummary([outbound(), outbound()]), '2 changes');
    assert.equal(groupSummary([outbound({ kind: 'Reduction' }), outbound({ kind: 'NoChange' })]), '1 would lower progress');
    assert.equal(groupSummary([outbound(), outbound({ kind: 'Unresolved' }), outbound({ kind: 'Failed' })]), '1 change · 2 problems');
    assert.equal(groupSummary([outbound({ kind: 'NoChange' })]), 'Up to date');
    assert.equal(groupSummary([outbound({ kind: 'Skipped' })]), 'Skipped by your rules');
    const [open, closed] = groupRows([outbound(), outbound({ sourceKey: 'b', sourceTitle: 'Other', kind: 'NoChange' })]);
    assert.deepEqual([open.collapsed, closed.collapsed], [false, true]);
});
test('an outbound change shows the count in bold and the status beneath, before and after', () => {
    assert.deepEqual(describeRow(outbound(), names), {
        provider: 'AniList', tracker: 'AniList', entry: 'Show', why: 'Jellyfin is ahead', proposedTone: '', whyTone: '',
        now: '3 of 12', nowNote: 'Watching', proposed: '→ 5 of 12', proposedNote: 'Watching', whyNote: '', nowQuiet: false, proposedQuiet: false
    });
});
test('a reduction proposes the lower count, in the warning tone', () => {
    const row = describeRow(outbound({ kind: 'Reduction', reason: 'reduction', plannedChange: { Progress: 8, Status: 'Watching' },
        plannedAgainst: { TotalEpisodes: 24, List: { Status: 'Watching', Progress: 14 } } }), names);
    assert.deepEqual([row.now, row.proposed, row.proposedNote, row.proposedTone, row.why], ['14 of 24', '→ 8 of 24', 'Would lower progress', 'attention', 'AniList is ahead']);
});
test('no change, not on the list, status-only trackers and status changes read plainly', () => {
    const none = describeRow(outbound({ kind: 'NoChange', reason: 'completed-on-tracker', plannedChange: null,
        plannedAgainst: { TotalEpisodes: 12, List: { Status: 'Completed', Progress: 12 } } }), names);
    assert.deepEqual([none.now, none.nowNote, none.proposed, none.proposedNote, none.nowQuiet, none.proposedQuiet], ['12 of 12', 'Completed', 'No change', '', false, true]);
    assert.equal(describeRow(outbound({ plannedAgainst: { TotalEpisodes: 12, List: null } }), names).now, 'Not on your list');
    const annict = describeRow(outbound({ provider: 'Annict', plannedAgainst: { TotalEpisodes: 12, List: { Status: 'PlanToWatch', Progress: 0 } },
        plannedChange: { Progress: 1, Status: 'Watching' } }), names);
    assert.deepEqual([annict.now, annict.proposed, annict.proposedNote], ['Plan to watch', '→ Watching', '']);
    const started = describeRow(outbound({ plannedAgainst: { TotalEpisodes: 12, List: { Status: 'PlanToWatch', Progress: 0 } },
        plannedChange: { Progress: 3, Status: 'Watching' } }), names);
    assert.deepEqual([started.now, started.nowNote, started.proposed, started.proposedNote], ['0 of 12', 'Plan to watch', '→ 3 of 12', 'Watching']);
});
test('problems are not checked, say in one line what went wrong on which tracker, and colour the reason', () => {
    const unresolved = describeRow(outbound({ kind: 'Unresolved', reason: 'resolve', resolutionNote: 'not-found', plannedChange: null, plannedAgainst: null,
        targetTitle: '', targetId: '', targetEpisode: null }), names);
    assert.deepEqual([unresolved.entry, unresolved.now, unresolved.proposed, unresolved.why, unresolved.whyNote, unresolved.whyTone],
        ['No matching entry', '—', 'Not checked', 'Not on AniList', '', 'attention']);
    assert.equal(describeRow(outbound({ kind: 'Unresolved', reason: 'resolve', resolutionNote: 'no-title-match', provider: 'Kitsu' }), names).why, 'No title match on Kitsu');
    assert.equal(describeRow(outbound({ kind: 'Unresolved', reason: 'resolve', resolutionNote: null }), names).why, 'Not on AniList');
    assert.equal(describeRow(outbound({ kind: 'Unresolved', reason: 'resolve', resolutionNote: 'brand-new' }), names).why, 'brand-new');
    assert.equal(describeRow(inbound({ kind: 'Unresolved', reason: 'no-anidb-id', sourceKey: '' }), names).why, 'The entry has no AniDB ID to match it with');
    assert.deepEqual([unresolved.nowQuiet, unresolved.proposedQuiet], [true, true]);
    assert.equal(describeRow(outbound({ kind: 'Failed', reason: 'rate-limited', plannedChange: null, plannedAgainst: null }), names).whyTone, 'error');
});
test('an inbound change says the tracker is ahead, with the episodes not yet watched as the note', () => {
    assert.deepEqual(describeRow(inbound({ plannedMark: { episodes: ['e1', 'e2', 'e3', 'e4'], marksSeason: true, unplayed: '3-4' } }), names).whyNote,
        'Episodes 3–4 not watched');
    assert.deepEqual(describeRow(inbound(), names), {
        provider: 'AniList', tracker: 'AniList', entry: 'Show', why: 'AniList is ahead', proposedTone: '', whyTone: '',
        now: 'Played 1-2', nowNote: 'In Jellyfin', proposed: '→ 4 episodes', proposedNote: 'Marked played, with the season',
        whyNote: 'Completed · 4 watched on AniList', nowQuiet: false, proposedQuiet: false
    });
    const one = describeRow(inbound({ plannedMark: { episodes: ['e1'], marksSeason: false } }), names);
    assert.deepEqual([one.proposed, one.proposedNote], ['→ 1 episode', 'Marked played']);
    assert.equal(describeRow(inbound({ plannedMark: { episodes: [], marksSeason: true } }), names).proposed, '→ The season');
    assert.equal(describeRow(inbound({ watchedFingerprint: '' }), names).now, 'Nothing played');
    assert.equal(describeRow(inbound({ kind: 'NoChange', reason: 'already-played' }), names).proposed, 'No change');
    assert.equal(describeRow(inbound({ trackerEntry: { status: 'On_hold', progress: 2 } }), names).whyNote, 'On hold · 2 watched on AniList');
});
test('resolver notes read as plain words; unknown ones pass through', () => {
    assert.equal(noteLabel('no-title-match'), 'No entry’s title matched the series');
    assert.equal(noteLabel('not-in-match'), 'The saved match does not cover this episode');
    assert.equal(noteLabel('something-new'), 'something-new');
    assert.equal(noteLabel(null), '');
});
test('unknown reasons pass through unchanged', () => {
    assert.equal(reasonLabel('something-new'), 'something-new');
    assert.equal(reasonLabel('no-anidb-id'), 'The entry has no AniDB ID to match it with');
    assert.equal(reasonLabel('manual-sync-rewatch'), 'Manual sync does not update rewatches');
});

test('apply refusals read as plain reasons', () => {
    assert.equal(reasonLabel('preview-stale'), 'Changed since the preview; refresh it to see it again');
    assert.equal(reasonLabel('cancelled'), 'Not applied because the apply was cancelled');
    assert.equal(reasonLabel('provider-stopped'), 'Not tried because the tracker stopped answering');
});
test('a status reason reads as a change or as already matching, by row kind', () => {
    assert.equal(reasonLabel('status', 'Change'), 'The status should match what you have watched');
    assert.equal(reasonLabel('status', 'NoChange'), 'The status already matches');
    assert.equal(describeRow(outbound({ kind: 'NoChange', reason: 'status', plannedChange: null }), names).why, 'The status already matches');
});

test('an episode range reads as words, with en dashes', () => {
    assert.equal(episodeRange('3-6'), 'Episodes 3–6');
    assert.equal(episodeRange('3'), 'Episode 3');
    assert.equal(episodeRange('1-2,6'), 'Episodes 1–2, 6');
    assert.equal(episodeRange(''), '');
    assert.equal(episodeRange(null), '');
});

test('an inbound preview has no reductions tab, and an applied preview opens on what was applied', () => {
    assert.deepEqual(filtersFor('ToTrackers', false).map(filter => filter.key), ['changes', 'reductions', 'problems', 'all']);
    assert.deepEqual(filtersFor('FromTrackers', false).map(filter => filter.key), ['changes', 'problems', 'all']);
    assert.deepEqual(filtersFor('FromTrackers', true).map(filter => filter.key), ['applied', 'changes', 'problems', 'all']);
    assert.equal(defaultFilter({ Change: 2, Applied: 2 }, { direction: 'ToTrackers', applied: true }), 'applied');
    assert.equal(defaultFilter({ Reduction: 2 }, { direction: 'FromTrackers' }), 'all');
    assert.equal(filterCount('applied', { Change: 3, Applied: 2 }), 2);
    assert.equal(rowsPath('op', 'applied', 0), 'AniDoki/preview/op/rows?offset=0&limit=200&applied=true');
});

const selectable = [
    { seq: 0, kind: 'Change', selectedByDefault: true }, { seq: 1, kind: 'Reduction', selectedByDefault: false },
    { seq: 2, kind: 'Change', selectedByDefault: true }
];
test('changes start selected and reductions do not', () => {
    assert.deepEqual([...defaultSelection(selectable)], [0, 2]);
    assert.deepEqual([...defaultSelection([])], []);
});
test('select all covers the rows of the current tab only', () => {
    const none = selectAll(defaultSelection(selectable), selectable, 'changes', false);
    assert.deepEqual([...none], []);
    assert.deepEqual([...selectAll(none, selectable, 'reductions', true)], [1]);
    assert.deepEqual([...selectAll(none, selectable, 'all', true)].sort(), [0, 1, 2]);
    assert.deepEqual([...selectAll(none, selectable, 'problems', true)], []);
});
test('the header checkbox is empty, none, some or all of the tab', () => {
    const selected = defaultSelection(selectable);
    assert.equal(filterSelection(selected, selectable, 'changes'), 'all');
    assert.equal(filterSelection(selected, selectable, 'reductions'), 'none');
    assert.equal(filterSelection(selected, selectable, 'all'), 'some');
    assert.equal(filterSelection(selected, selectable, 'problems'), 'empty');
    assert.equal(filterSelection(selected, selectable, 'applied'), 'empty');
});
test('the footer counts what is selected and names the action, per direction', () => {
    const outboundSummary = selectionSummary(defaultSelection(selectable), selectable, 'ToTrackers');
    assert.deepEqual(outboundSummary, { count: 2, reductions: [], status: '2 of 3 changes selected', button: 'Apply 2 changes', needsConfirm: false });
    const one = selectionSummary(new Set([1]), selectable, 'ToTrackers');
    assert.deepEqual(one, { count: 1, reductions: [1], status: '1 of 3 changes selected', button: 'Apply 1 change', needsConfirm: true });
    const seasons = [{ seq: 0, kind: 'Change', selectedByDefault: true }, { seq: 1, kind: 'Change', selectedByDefault: true }];
    assert.equal(selectionSummary(new Set([0]), seasons, 'FromTrackers').status, '1 of 2 seasons selected');
    assert.equal(selectionSummary(new Set([0]), seasons, 'FromTrackers').button, 'Mark 1 season played');
    assert.equal(selectionSummary(new Set([0, 1]), seasons, 'FromTrackers').button, 'Mark 2 seasons played');
    assert.equal(selectionSummary(new Set([0, 1]), [{ seq: 0, kind: 'Change' }], 'ToTrackers').count, 1);
});

test('each result reads as a short label with its tone and icon', () => {
    const label = (kind, step, direction = 'ToTrackers', provider = 'Kitsu') => resultLabel(kind ? { kind, step } : null, names, provider, direction, true);
    assert.deepEqual(label('Confirmed', 'progress'), { text: 'Updated', tone: 'success', icon: 'check' });
    assert.deepEqual(label('Confirmed', 'mark-played', 'FromTrackers'), { text: 'Marked played', tone: 'success', icon: 'check' });
    assert.deepEqual(label('NoChange', 'progress'), { text: 'Already up to date', tone: 'neutral', icon: 'dash' });
    assert.deepEqual(label('Skipped', 'preview-stale'), { text: 'Changed since the preview', tone: 'attention', icon: 'skip' });
    assert.deepEqual(label('Skipped', 'cancelled'), { text: 'Not reached', tone: 'attention', icon: 'skip' });
    assert.deepEqual(label('Skipped', 'provider-stopped'), { text: 'Not tried', tone: 'attention', icon: 'skip' });
    assert.deepEqual(label('Failed', 'rejected'), { text: 'Refused by Kitsu', tone: 'error', icon: 'cross' });
    assert.deepEqual(label('Failed', 'unauthorized'), { text: 'Kitsu sign-in rejected', tone: 'error', icon: 'cross' });
    assert.deepEqual(label('Failed', 'rate-limited'), { text: 'Kitsu is limiting requests', tone: 'error', icon: 'cross' });
    assert.deepEqual(label('Failed', 'transient'), { text: 'Kitsu didn’t respond', tone: 'error', icon: 'cross' });
    assert.deepEqual(label('Failed', 'something-else'), { text: 'Failed', tone: 'error', icon: 'cross' });
    assert.deepEqual(label('Unconfirmed', 'progress'), { text: 'Not confirmed', tone: 'attention', icon: 'triangle' });
    assert.deepEqual(label(null), { text: 'Waiting', tone: 'neutral', icon: 'clock' });
    assert.equal(resultLabel(null, names, 'Kitsu', 'ToTrackers', false), null);
});

test('a preview that can no longer be applied says why in one line', () => {
    assert.equal(outOfDateLabel('Succeeded'), 'This preview is out of date. Refresh it to apply changes.');
    for (const state of ['Partial', 'Cancelled', 'Interrupted'])
        assert.equal(outOfDateLabel(state), 'This preview didn’t finish cleanly. Refresh it to apply changes.');
});
