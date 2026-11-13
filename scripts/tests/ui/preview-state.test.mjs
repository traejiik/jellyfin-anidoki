import test from 'node:test';
import assert from 'node:assert/strict';
import { defaultFilter, describeRow, emptyLabel, filterCount, filters, groupRows, groupSummary, noteLabel, pageLabel, paged, reasonLabel, rowsPath } from '../../../jellyfin-anidoki/Configuration/PreviewStateJs.js';

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
        provider: 'AniList', tracker: 'AniList', entry: 'Show', why: 'You have watched more in Jellyfin', proposedTone: '', whyTone: '',
        now: '3 of 12', nowNote: 'Watching', proposed: '→ 5 of 12', proposedNote: 'Watching', whyNote: '', nowQuiet: false, proposedQuiet: false
    });
});
test('a reduction proposes the lower count, in the warning tone', () => {
    const row = describeRow(outbound({ kind: 'Reduction', reason: 'reduction', plannedChange: { Progress: 8, Status: 'Watching' },
        plannedAgainst: { TotalEpisodes: 24, List: { Status: 'Watching', Progress: 14 } } }), names);
    assert.deepEqual([row.now, row.proposed, row.proposedNote, row.proposedTone, row.why], ['14 of 24', '→ 8 of 24', 'Would lower progress', 'attention', 'The tracker has more watched than Jellyfin']);
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
test('problems are not checked, keep their note, and colour the reason', () => {
    const unresolved = describeRow(outbound({ kind: 'Unresolved', reason: 'resolve', resolutionNote: 'not-found', plannedChange: null, plannedAgainst: null,
        targetTitle: '', targetId: '', targetEpisode: null }), names);
    assert.deepEqual([unresolved.entry, unresolved.now, unresolved.proposed, unresolved.why, unresolved.whyNote, unresolved.whyTone],
        ['No matching entry', '—', 'Not checked', 'Not found on the tracker', 'The tracker has no matching entry', 'attention']);
    assert.deepEqual([unresolved.nowQuiet, unresolved.proposedQuiet], [true, true]);
    assert.equal(describeRow(outbound({ kind: 'Failed', reason: 'rate-limited', plannedChange: null, plannedAgainst: null }), names).whyTone, 'error');
});
test('an inbound change says what Jellyfin would mark, with the tracker state as the reason note', () => {
    assert.deepEqual(describeRow(inbound(), names), {
        provider: 'AniList', tracker: 'AniList', entry: 'Show', why: 'Watched on the tracker, not yet played in Jellyfin', proposedTone: '', whyTone: '',
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
test('a status reason reads as a change or as already matching, by row kind', () => {
    assert.equal(reasonLabel('status', 'Change'), 'The status should match what you have watched');
    assert.equal(reasonLabel('status', 'NoChange'), 'The status already matches');
    assert.equal(describeRow(outbound({ kind: 'NoChange', reason: 'status', plannedChange: null }), names).why, 'The status already matches');
});
