import test from 'node:test';
import assert from 'node:assert/strict';
import * as editor from '../../../jellyfin-anidoki/Configuration/MatchEditorStateJs.js';

const entry = id => ({ id, title: `Entry ${id}` });
const range = (sourceStart, sourceEnd, id, targetStart = 1) => ({ sourceStart, sourceEnd, target: entry(id), targetStart });
const saved = (ranges, exceptions = [], revision = 3) => ({ revision, mapping: { ranges, exceptions }, note: 'split by AniList' });
const fourByTwelve = () => saved([range(1, 12, 101), range(13, 24, 102), range(25, 36, 103), range(37, 48, 104)]);
const rules = state => editor.check(state.draft).map(issue => issue.rule);
const messages = state => editor.check(state.draft).map(issue => issue.message);

test('a saved match opens as a clean draft that expects its revision', () => {
    const state = editor.createEditor(fourByTwelve());
    assert.equal(editor.isDirty(state), false);
    assert.deepEqual(editor.check(state.draft), []);
    assert.deepEqual(editor.saveRequest(state), { expectedRevision: 3, ...fourByTwelve().mapping, note: 'split by AniList' });
});

test('nothing saved expects revision 0', () => {
    const state = editor.createEditor(null);
    assert.equal(editor.saveRequest(state).expectedRevision, 0);
    assert.deepEqual(state.draft.ranges, []);
});

test('a new range continues from the last one', () => {
    let state = editor.addRange(editor.createEditor(null));
    assert.equal(state.draft.ranges[0].sourceStart, '1');
    state = editor.updateRow(state, state.draft.ranges[0].key, 'sourceEnd', '12');
    state = editor.addRange(state);
    assert.deepEqual([state.draft.ranges[1].sourceStart, state.draft.ranges[1].sourceEnd, state.draft.ranges[1].targetStart], ['13', '', '1']);
});

test('adding and removing a range makes the draft dirty, and putting it back makes it clean', () => {
    const start = editor.createEditor(fourByTwelve());
    const removed = editor.removeRow(start, start.draft.ranges[3].key);
    assert.equal(removed.draft.ranges.length, 3);
    assert.equal(editor.isDirty(removed), true);
    let back = editor.addRange(removed);
    const row = back.draft.ranges[3].key;
    back = editor.updateRow(back, row, 'sourceEnd', '48');
    back = editor.updateRow(back, row, 'target', entry(104));
    assert.equal(editor.isDirty(back), false, 'same numbers and entry, so nothing to save');
});

test('typing a number the same as the saved one is not a change, and a title alone is not either', () => {
    let state = editor.createEditor(fourByTwelve());
    state = editor.updateRow(state, state.draft.ranges[0].key, 'sourceEnd', ' 12 ');
    state = editor.updateRow(state, state.draft.ranges[1].key, 'target', { id: 102, title: 'Renamed' });
    assert.equal(editor.isDirty(state), false);
    assert.equal(editor.isDirty(editor.setNote(state, 'changed')), true);
});

test('only known fields of a row can be changed', () => {
    const state = editor.createEditor(fourByTwelve());
    const row = state.draft.ranges[0].key;
    assert.equal(editor.updateRow(state, row, 'key', 'other').draft.ranges[0].key, row);
    assert.equal('bogus' in editor.updateRow(state, row, 'bogus', 1).draft.ranges[0], false);
});

test('discard returns to the saved match and clears a conflict', () => {
    let state = editor.createEditor(fourByTwelve());
    state = editor.markConflict(editor.removeRow(state, state.draft.ranges[0].key), 4);
    assert.deepEqual(state.conflict, { currentRevision: 4 });
    state = editor.discard(state);
    assert.equal(editor.isDirty(state), false);
    assert.equal(state.conflict, null);
});

test('a conflict keeps the draft until the user reloads, and a reload starts from the new revision', () => {
    let state = editor.createEditor(fourByTwelve());
    state = editor.markConflict(editor.removeRow(state, state.draft.ranges[0].key), 4);
    assert.equal(state.draft.ranges.length, 3, 'the user’s draft is not thrown away by the 409');
    const reloaded = editor.createEditor(saved([range(1, 48, 101)], [], 4));
    assert.equal(editor.saveRequest(reloaded).expectedRevision, 4);
    assert.equal(reloaded.conflict, null);
});

test('saving keeps the draft and makes it the saved match', () => {
    let state = editor.setNote(editor.createEditor(null), '  ');
    state = editor.addRange(state);
    state = editor.updateRow(state, state.draft.ranges[0].key, 'sourceEnd', '12');
    state = editor.updateRow(state, state.draft.ranges[0].key, 'target', entry(101));
    assert.equal(editor.saveRequest(state).note, null, 'a blank note is not saved');
    state = editor.markSaved(state, 1);
    assert.equal(editor.isDirty(state), false);
    assert.equal(editor.saveRequest(state).expectedRevision, 1);
});

test('replacing the draft with the automatic answer keeps the revision to expect', () => {
    const state = editor.replaceDraft(editor.createEditor(fourByTwelve()), { ranges: [range(1, 48, 101)], exceptions: [] });
    assert.equal(editor.saveRequest(state).expectedRevision, 3);
    assert.equal(state.draft.ranges.length, 1);
    assert.equal(editor.isDirty(state), true);
});

test('blank numbers and missing entries are named by row before anything else', () => {
    let state = editor.addException(editor.addRange(editor.createEditor(null)));
    state = editor.updateRow(state, state.draft.exceptions[0].key, 'kind', 'target');
    const issues = editor.check(state.draft);
    assert.deepEqual(issues.map(issue => issue.rule), ['input', 'input', 'input', 'input']);
    assert.deepEqual(issues.map(issue => issue.message), [
        'Range 1: choose an entry.', 'An exception: enter the episode.', 'An exception: choose an entry.', 'An exception: enter the entry\'s episode.'
    ]);
    assert.equal(issues[0].row, state.draft.ranges[0].key);
});

test('an ignore exception needs only its episode', () => {
    let state = editor.addException(editor.createEditor(fourByTwelve()));
    state = editor.updateRow(state, state.draft.exceptions[0].key, 'sourceEpisode', '5');
    assert.deepEqual(editor.check(state.draft), []);
    assert.deepEqual(editor.toMapping(state.draft).exceptions, [{ sourceEpisode: 5, kind: 'ignore' }]);
});

// V2

test('V2: a range must run forwards, and only the last may be open', () => {
    assert.deepEqual(messages(editor.createEditor(saved([range(12, 1, 101)]))), ['Range 1 must run forwards from episode 0 or later.']);
    assert.deepEqual(messages(editor.createEditor(saved([range(1, null, 101), range(13, 24, 102)]))), ['Range 1 is open, but only the last range can be.']);
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 12, 101), range(13, null, 102)]))), []);
});

test('V2: episode numbers stop at a million, as on the server', () => {
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 1_000_001, 101)]))), ['V2']);
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 1_000_000, 101)]))), []);
    assert.deepEqual(rules(editor.createEditor(saved([], [{ sourceEpisode: 2_000_000, kind: 'ignore' }]))), ['V2']);
});

test('V2 is reported alone, as the server does, since the later checks rely on it', () => {
    assert.deepEqual(rules(editor.createEditor(saved([range(1, null, 101), range(1, 12, 101)]))), ['V2']);
});

// V3

test('V3: ranges may not share a source episode, in any order', () => {
    assert.deepEqual(messages(editor.createEditor(saved([range(13, 24, 102), range(1, 13, 101)]))), ['The ranges starting at episodes 1 and 13 overlap.']);
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 12, 101), range(13, 24, 102)]))), []);
});

test('V3: one exception per episode', () => {
    const state = editor.createEditor(saved([range(1, 12, 101)], [{ sourceEpisode: 5, kind: 'ignore' }, { sourceEpisode: 5, kind: 'target', target: entry(900), targetEpisode: 1 }]));
    assert.deepEqual(messages(state), ['Episode 5 has more than one exception.']);
});

// V5

test('V5: two ranges sending episodes to the same entry episodes', () => {
    const state = editor.createEditor(saved([range(1, 12, 101), range(13, 24, 101)]));
    assert.deepEqual(messages(state), ['More than one episode goes to episode 1 of "Entry 101".']);
});

test('V5: the same entry, carried on further, is fine', () => {
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 12, 101), range(13, 24, 101, 13)]))), []);
});

test('V5: an exception landing where a range already lands', () => {
    const state = editor.createEditor(saved([range(1, 12, 101)], [{ sourceEpisode: 13, kind: 'target', target: entry(101), targetEpisode: 5 }]));
    assert.deepEqual(messages(state), ['More than one episode goes to episode 5 of "Entry 101".']);
});

test('V5: an exception taking over the episode that would have landed there is fine', () => {
    const state = editor.createEditor(saved([range(1, 12, 101)], [{ sourceEpisode: 5, kind: 'target', target: entry(101), targetEpisode: 5 }]));
    assert.deepEqual(rules(state), []);
});

test('V5: an exception moving the colliding episode out of the way clears it, as on the server', () => {
    // Episode 13 would land on 101 ep 1 like episode 1, but an exception sends 13 elsewhere; 14 still collides with 2.
    const state = editor.createEditor(saved([range(1, 12, 101), range(13, 14, 101)], [{ sourceEpisode: 13, kind: 'ignore' }]));
    assert.deepEqual(messages(state), ['More than one episode goes to episode 2 of "Entry 101".']);
    const cleared = editor.createEditor(saved([range(1, 12, 101), range(13, 14, 101)], [{ sourceEpisode: 13, kind: 'ignore' }, { sourceEpisode: 14, kind: 'ignore' }]));
    assert.deepEqual(rules(cleared), []);
});

test('V5: two exceptions on one entry episode', () => {
    const state = editor.createEditor(saved([], [
        { sourceEpisode: 0, kind: 'target', target: entry(900), targetEpisode: 1 }, { sourceEpisode: 1, kind: 'target', target: entry(900), targetEpisode: 1 }
    ]));
    assert.deepEqual(rules(state), ['V5']);
});

test('V5: different entries never collide, and an open range is checked to its start', () => {
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 12, 101), range(13, 24, 102)]))), []);
    assert.deepEqual(rules(editor.createEditor(saved([range(1, 12, 101), range(13, null, 101, 10)]))), ['V5']);
});

test('V5 stays quick on wide ranges', () => {
    const start = Date.now();
    editor.check(editor.createEditor(saved([range(0, 999_999, 101), range(1_000_000, 1_000_000, 101, 2)])).draft);
    assert.ok(Date.now() - start < 500);
});

// The source list

test('a search or filter starts from the first page, and unknown filters are ignored', () => {
    let list = editor.setPage(editor.createList(), 3, 5);
    assert.equal(list.page, 3);
    assert.equal(editor.setSearch(list, 'flcl').page, 1);
    assert.equal(editor.setFilter(list, 'corrections').page, 1);
    assert.equal(editor.setFilter(list, 'needs-match'), list, 'PR 14 adds that filter; until then it is not offered');
});

test('the page stays between the first and the last', () => {
    assert.equal(editor.setPage(editor.createList(), 0, 4).page, 1);
    assert.equal(editor.setPage(editor.createList(), 9, 4).page, 4);
    assert.equal(editor.pageCount({ total: 101, pageSize: 50 }), 3);
    assert.equal(editor.pageCount({ total: 0, pageSize: 50 }), 1);
});

test('the list path leaves out what is not set', () => {
    assert.equal(editor.listPath('AniDoki/matches', editor.createList()), 'AniDoki/matches/sources');
    const list = editor.setPage(editor.setFilter(editor.setSearch(editor.createList(), ' Steel Ball '), 'matched'), 2, 3);
    assert.equal(editor.listPath('AniDoki/matches', list), 'AniDoki/matches/sources?search=Steel+Ball&filter=matched&page=2');
});

test('source keys are encoded one segment at a time', () => {
    assert.equal(editor.routePath('AniDoki/matches', 'season:3b69:1', 'AniList', 'preview'), 'AniDoki/matches/season%3A3b69%3A1/AniList/preview');
});

test('provider states and rows read as words', () => {
    assert.equal(editor.stateLabel({ provider: 'AniList', sharedRevision: 2, corrections: 3 }), 'Shared r2 · 3 corrections');
    assert.equal(editor.stateLabel({ provider: 'AniList', personalRevision: 1 }), 'Personal');
    assert.equal(editor.stateLabel({ provider: 'AniList' }), 'Automatic');
    assert.equal(editor.resolutionBadge(null), 'Automatic');
    assert.equal(editor.resolutionBadge({ revision: 4 }), 'Shared r4');
    assert.deepEqual(editor.describeRow({ outcome: 'mapped', target: entry(101), targetEpisode: 3 }), { text: 'Entry 101 · episode 3', tone: '' });
    assert.deepEqual(editor.describeRow({ outcome: 'past-entry-end', target: entry(101), targetEpisode: 13 }), { text: 'Past the entry’s last episode: Entry 101 · episode 13', tone: 'error' });
    assert.deepEqual(editor.describeRow({ outcome: 'not-in-match' }), { text: 'Not in this match', tone: 'error' });
});

test('an orphan reads as what was saved and when', () => {
    const orphan = { kind: 'Season', providers: [{ provider: 'AniList', sharedRevision: 2, corrections: 1 }], updatedUtc: '2026-11-07T10:00:00Z' };
    assert.match(editor.orphanSummary(orphan, name => name === 'AniList' ? 'AniList' : name), /^Season · AniList: Shared r2 · 1 correction · last saved /);
    assert.equal(editor.orphanSummary({ kind: 'Movie' }), 'Movie · Only history is left');
});
