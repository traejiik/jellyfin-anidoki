import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';

const state = await importConfiguration('PopupStateJs.js');
const { toDisplay, toRaw, scoreScale, sameScale, scoreText, statusLabel, sharedStatusLabel, buildRows, defaultRow, rowTargets,
    editableTargets, pillSummary, allSummary, airedLabel, seriesStatusLabel, entryUrl } = state;

// Fixtures shaped like SeriesViewContract (Progress/ProgressContracts.cs).
const entry = (id, extra = {}) => ({ id, alternativeId: null, title: `Entry ${id}`, totalEpisodes: 12, firstEpisode: 1, lastEpisode: 12,
    status: 'Watching', progress: 3, score: null, rewatching: false, jellyfinProgress: 3, ...extra });
const seasonTracker = (provider, entries, extra = {}) => ({ provider, origin: 'Automatic', unresolved: null, error: null, entries, ...extra });
const season = (number, trackers, extra = {}) => ({ sourceKey: `series:s${number}`, season: number, episodes: [1, 2, 3], watched: [1, 2, 3],
    lastPlayedUtc: null, trackers, ...extra });
const account = (provider, scoreFormat = 'Point10Decimal') => ({ provider, supportsScore: scoreFormat !== null, scoreFormat });
const view = (seasons, accounts = [account('AniList')], extra = {}) => ({ id: 'series', name: 'Long Show', isMovie: false, year: 1998,
    endYear: 1999, communityRating: 8.4, officialRating: 'TV-14', status: 'Ended', overview: null, premiereDate: null, images: [],
    episodeCount: 26, defaultSeason: null, matchesLinkAvailable: true, trackers: accounts, seasons, ...extra });

test('score conversions match ScoreScale', () => {
    const table = [[85, 'Point10Decimal', 8.5], [85, 'Point10Half', 8.5], [83, 'Point10Half', 8.5], [5, 'Point10Half', 1], [85, 'Point10', 9],
        [85, 'Point5', 4], [35, 'Point3', 1], [36, 'Point3', 2], [61, 'Point3', 3], [85, 'Point100', 85]];
    for (const [raw, format, display] of table)
        assert.equal(toDisplay(raw, format), display, `${raw} as ${format}`);
    assert.equal(toDisplay(null, 'Point10'), null);
    assert.equal(toRaw(4, 'Point5'), 80);
    assert.equal(toRaw(2, 'Point3'), 60);
    assert.equal(toRaw(8.5, 'Point10Half'), 85);
    assert.equal(toRaw(7.3, 'Point10Decimal'), 73);
    assert.equal(toRaw(0.5, 'Point10Half'), null);
    assert.equal(toRaw(7.25, 'Point10Decimal'), null);
    assert.equal(toRaw(11, 'Point10'), null);
    assert.deepEqual(scoreScale('Point10Half'), { max: 10, step: 0.5, min: 1 });
    assert.deepEqual(scoreScale('Point100'), { max: 100, step: 1, min: 1 });
    assert.equal(sameScale('Point10Decimal', 'Point10Half'), true);
    assert.equal(sameScale('Point10', 'Point5'), false);
});

test('score text', () => {
    assert.equal(scoreText(85, 'Point10Decimal'), '8.5/10');
    assert.equal(scoreText(70, 'Point10Decimal'), '7/10');
    assert.equal(scoreText(null, 'Point10'), 'no rating');
    assert.equal(scoreText(85, 'Point3'), 'Liked');
    assert.equal(scoreText(80, 'Point5'), '4/5');
    assert.equal(scoreText(85, 'Point100'), '85/100');
});

test('status labels follow each tracker', () => {
    assert.equal(statusLabel('OnHold', 'AniList'), 'Paused');
    assert.equal(statusLabel('OnHold', 'Kitsu'), 'On hold');
    assert.equal(statusLabel('PlanToWatch', 'Kitsu'), 'Plan to watch');
    assert.equal(sharedStatusLabel('OnHold', ['AniList', 'Kitsu']), 'Paused or on hold');
    assert.equal(sharedStatusLabel('OnHold', ['AniList']), 'Paused');
    assert.equal(sharedStatusLabel('Watching', ['AniList', 'Kitsu']), 'Watching');
    assert.equal(statusLabel(null, 'Mal'), 'Not on your list');
});

test('one row per season, none split', () => {
    const rows = buildRows(view([season(1, [seasonTracker('AniList', [entry(1)])]), season(2, [seasonTracker('AniList', [entry(2)])])]));
    assert.deepEqual(rows.map(row => [row.label, row.split, row.season]), [['Season 1', false, 1], ['Season 2', false, 2]]);
    assert.equal(rows[0].key, 'series:s1:1');
});

const splitView = () => view([season(3, [
    seasonTracker('AniList', [entry(1, { title: 'Attack on Titan Season 3', lastEpisode: 12 }),
        entry(2, { title: 'Attack on Titan Season 3 Part 2', firstEpisode: 13, lastEpisode: 22, totalEpisodes: 10 })]),
    seasonTracker('Kitsu', [entry(9, { title: 'Attack on Titan Season 3', lastEpisode: 22, totalEpisodes: 22 })])
])], [account('AniList'), account('Kitsu', 'Point10Half')]);

test('rows follow the tracker that splits a season', () => {
    const data = splitView(), rows = buildRows(data);
    assert.deepEqual(rows.map(row => [row.label, row.split, row.group]), [['Ep 1–12', true, 'Season 3'], ['Ep 13–22', true, 'Season 3']]);
    assert.equal(rows[1].heading, 'Season 3, episodes 13–22: Attack on Titan Season 3 Part 2');
    const targets = rowTargets(data, rows[1]);
    assert.deepEqual(targets.map(target => [target.provider, target.entry.id, target.state]), [['AniList', 2, 'ok'], ['Kitsu', 9, 'ok']]);
});

test('a movie has one row and no seasons box', () => {
    const movie = view([season(null, [seasonTracker('AniList', [entry(5, { totalEpisodes: 1, firstEpisode: null, lastEpisode: null })])],
        { sourceKey: 'movie:m', episodes: [1] })], undefined, { isMovie: true });
    const rows = buildRows(movie);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].label, 'Movie');
    assert.equal(rows[0].split, false);
});

test('default row is the opened season', () => {
    const data = view([season(1, [seasonTracker('AniList', [entry(1)])]), season(2, [seasonTracker('AniList', [entry(2)])])]);
    data.defaultSeason = 'series:s2';
    assert.equal(defaultRow(data, buildRows(data)).season, 2);
    data.defaultSeason = 'series:unknown';
    assert.equal(defaultRow(data, buildRows(data)).season, 1);
});

test('failed and unmatched trackers are not targets', () => {
    const data = view([season(1, [seasonTracker('AniList', [], { error: 'Transient' }), seasonTracker('Kitsu', [], { unresolved: 'No match' }),
        seasonTracker('Mal', [entry(4)])])], [account('AniList'), account('Kitsu'), account('Mal', 'Point10')]);
    const targets = rowTargets(data, buildRows(data)[0]);
    assert.deepEqual(targets.map(target => [target.provider, target.state]), [['AniList', 'error'], ['Kitsu', 'unmatched'], ['Mal', 'ok']]);
    assert.equal(targets[0].error, 'Transient');
    assert.deepEqual(editableTargets(targets).map(target => target.provider), ['Mal']);
    assert.equal(pillSummary(targets[0]).status, "Couldn't read AniList");
    assert.equal(pillSummary(targets[1]).status, 'No match for this season');
});

const target = (provider, extra = {}, format = 'Point10Decimal') => ({ provider, account: account(provider, format), entry: entry(1, extra), state: 'ok', error: null });

test('closed pill text', () => {
    assert.deepEqual(pillSummary(target('AniList', { totalEpisodes: 26 })), { status: 'Watching', progress: 3, total: 26, text: '3/26' });
    assert.equal(pillSummary(target('AniList', { totalEpisodes: 0, progress: 5 })).text, '5/?');
    const unlisted = pillSummary(target('AniList', { status: null, progress: null, totalEpisodes: 26 }));
    assert.equal(unlisted.status, 'Not on your list');
    assert.equal(unlisted.text, '0/26');
    assert.equal(pillSummary(target('AniList', { totalEpisodes: 26 }), { status: 'Completed', progress: 26, score: null, clearScore: false }).text, '26/26');
});

test('all trackers summary', () => {
    const same = [target('AniList', { totalEpisodes: 26 }), target('Kitsu', { totalEpisodes: 26 })];
    assert.deepEqual(allSummary(same, {}), { status: 'Watching', text: '3/26' });
    const differ = [target('AniList', { totalEpisodes: 26 }), target('Kitsu', { totalEpisodes: 26, status: 'Completed', progress: 4 })];
    assert.deepEqual(allSummary(differ, {}), { status: 'Mixed', text: '3–4/26' });
});

test('aired season', () => {
    assert.equal(airedLabel('1998-04-03T00:00:00Z'), 'Spring 1998');
    assert.equal(airedLabel('2016-07-01T00:00:00Z'), 'Summer 2016');
    assert.equal(airedLabel('2026-01-10T00:00:00Z'), 'Winter 2026');
    assert.equal(airedLabel('2026-10-05T00:00:00Z'), 'Fall 2026');
    assert.equal(airedLabel(null), null);
    assert.equal(seriesStatusLabel('Continuing'), 'Airing');
    assert.equal(seriesStatusLabel('Ended'), 'Finished');
    assert.equal(seriesStatusLabel(null), null);
});

test('entry urls', () => {
    assert.equal(entryUrl('AniList', { id: 1 }), 'https://anilist.co/anime/1');
    assert.equal(entryUrl('Mal', { id: 2 }), 'https://myanimelist.net/anime/2');
    assert.equal(entryUrl('Kitsu', { id: 3 }), 'https://kitsu.app/anime/3');
    assert.equal(entryUrl('Shikimori', { id: 4 }), 'https://shikimori.io/animes/4');
    assert.equal(entryUrl('Simkl', { id: 5 }), 'https://simkl.com/anime/5');
    assert.equal(entryUrl('Annict', { id: 6 }), 'https://annict.com/works/6');
});

// --- Task 3: editor drafts, notes and the sync buttons ---
const { editorModel, setProgress, setStatus, setScore, markCompleted, isDirty, syncButtons, planSyncUp, planSyncDown } = state;
const one = (extra = {}, format = 'Point10Decimal', provider = 'AniList') => [target(provider, extra, format)];

test('raising progress from plan to watch starts watching', () => {
    const targets = one({ status: 'PlanToWatch', progress: 0 });
    const drafts = setProgress(targets, {}, 1);
    assert.equal(drafts.AniList.status, 'Watching');
    assert.equal(drafts.AniList.progress, 1);
    assert.equal(isDirty(targets[0], drafts.AniList), true);
});

test('completed fills progress and plan to watch empties it', () => {
    const targets = one({ progress: 5 });
    assert.equal(setStatus(targets, {}, 'Completed').AniList.progress, 12);
    assert.equal(setStatus(targets, {}, 'PlanToWatch').AniList.progress, 0);
    assert.equal(markCompleted(targets, {}).AniList.status, 'Completed');
});

test('an edit back to the saved state is no longer a draft', () => {
    const targets = one({ progress: 5 });
    assert.deepEqual(setProgress(targets, setProgress(targets, {}, 6), 5), {});
});

test('progress is clamped to the total', () => {
    const targets = one();
    assert.equal(setProgress(targets, {}, 15).AniList.progress, 12);
    assert.equal(setProgress(targets, {}, -1).AniList.progress, 0);
});

test('unknown totals accept any progress', () => {
    const targets = one({ totalEpisodes: 0, progress: 5 });
    const drafts = setProgress(targets, {}, 40);
    assert.equal(drafts.AniList.progress, 40);
    const model = editorModel(targets, drafts, { all: false });
    assert.equal(model.suggestComplete, false);
    assert.equal(model.fields.progress.max, null);
    assert.equal(setStatus(targets, drafts, 'Completed').AniList.progress, 40);
});

test('an unlisted entry becomes watching when progress rises', () => {
    const targets = one({ status: null, progress: null });
    assert.equal(setProgress(targets, {}, 2).AniList.status, 'Watching');
    const rated = setScore(targets, {}, 7);
    assert.equal(rated.AniList.status, 'PlanToWatch');
    assert.equal(rated.AniList.score, 70);
    const plan = planSyncUp(targets, rated, { isMovie: false, itemId: 'series' });
    assert.equal(plan.edit.edits[0].shown.status, null);
    assert.equal(plan.edit.edits[0].status, 'PlanToWatch');
});

test('clicking the current star clears the rating', () => {
    const stars = one({ score: 80 }, 'Point5');
    assert.deepEqual(pick(setScore(stars, {}, 4, { toggle: true }).AniList), { score: null, clearScore: true });
    const ten = one({ score: 70 }, 'Point10');
    assert.deepEqual(pick(setScore(ten, {}, 8).AniList), { score: 80, clearScore: false });
    assert.equal(setScore(ten, {}, 7).AniList, undefined, 'typing the saved value is no change');
    assert.deepEqual(pick(setScore(ten, {}, null).AniList), { score: null, clearScore: true });
});
const pick = draft => ({ score: draft.score, clearScore: draft.clearScore });

test('mark completed appears at the last episode', () => {
    const targets = one({ progress: 11 });
    assert.equal(editorModel(targets, setProgress(targets, {}, 12), { all: false }).suggestComplete, true);
    assert.equal(editorModel(targets, setStatus(targets, {}, 'Completed'), { all: false }).suggestComplete, false);
});

test('lowering is allowed with a warning', () => {
    const targets = [target('AniList', { progress: 8 }), target('Kitsu', { progress: 8 }, 'Point10Half')];
    assert.equal(editorModel(targets.slice(0, 1), setProgress(targets.slice(0, 1), {}, 5), { all: false }).lowerNote, 'Lowers AniList from 8 to 5');
    assert.equal(editorModel(targets, setProgress(targets, {}, 5), { all: true }).lowerNote, 'Lowers AniList from 8 to 5. Lowers Kitsu from 8 to 5');
});

const pair = (aniList = {}, kitsu = {}, kitsuFormat = 'Point10Half') => [target('AniList', aniList), target('Kitsu', kitsu, kitsuFormat)];

test('mixed note names one differing field', () => {
    assert.equal(editorModel(pair({}, { status: 'Completed' }), {}, { all: true }).mixedNote,
        'The trackers differ in status: AniList - Watching, Kitsu - Completed');
    assert.equal(editorModel(pair({ progress: 5 }, { progress: 8 }), {}, { all: true }).mixedNote,
        'The trackers differ in watch progress: AniList - 5 of 12, Kitsu - 8 of 12');
    assert.equal(editorModel(pair({ score: 70 }, { score: 80 }), {}, { all: true }).mixedNote,
        'The trackers differ in rating: AniList - 7/10, Kitsu - 8/10');
    assert.equal(editorModel(pair(), {}, { all: true }).mixedNote, '');
});

test('mixed note lists several fields', () => {
    assert.equal(editorModel(pair({ score: 70 }, { status: 'Completed', progress: 8, score: 80 }), {}, { all: true }).mixedNote,
        'The trackers differ in status, watch progress and rating');
});

test('all trackers hides rating when scales differ', () => {
    const model = editorModel(pair({ score: 70 }, { score: 80 }, 'Point5'), {}, { all: true });
    assert.equal(model.fields.rating, null);
    assert.equal(model.mixedNote, '');
});

test('all trackers rating uses the coarsest step', () => {
    const rating = editorModel(pair({ score: 70 }, { score: 80 }), {}, { all: true }).fields.rating;
    assert.deepEqual({ kind: rating.kind, step: rating.step, min: rating.min, max: rating.max, value: rating.value },
        { kind: 'number', step: 0.5, min: 1, max: 10, value: 'Mixed' });
    assert.equal(editorModel(one({}, 'Point5'), {}, { all: false }).fields.rating.kind, 'stars');
    assert.equal(editorModel(one({}, 'Point3'), {}, { all: false }).fields.rating.kind, 'faces');
    assert.equal(editorModel(one({}, null, 'Annict'), {}, { all: false }).fields.rating, null);
});

test('all trackers hides progress when totals differ', () => {
    const model = editorModel(pair({ totalEpisodes: 10 }, { totalEpisodes: 22 }), {}, { all: true });
    assert.equal(model.fields.progress, null);
    assert.equal(model.fields.status.value, 'Watching');
});

test('mixed status edits set every tracker', () => {
    const targets = pair({}, { status: 'Completed' });
    const model = editorModel(targets, {}, { all: true });
    assert.equal(model.fields.status.value, 'Mixed');
    assert.deepEqual(model.statuses[0], ['Mixed', 'Mixed']);
    assert.ok(model.statuses.some(([value, label]) => value === 'OnHold' && label === 'Paused or on hold'));
    const drafts = setStatus(targets, {}, 'Dropped');
    assert.deepEqual([drafts.AniList.status, drafts.Kitsu.status], ['Dropped', 'Dropped']);
    assert.deepEqual(setStatus(targets, {}, 'Mixed'), {});
});

test('a rewatching entry is read-only', () => {
    const targets = one({ rewatching: true });
    const model = editorModel(targets, {}, { all: false });
    assert.equal(model.readOnly, true);
    assert.equal(model.readOnlyReason, 'This entry is being rewatched. Change it on AniList for now');
    assert.deepEqual(setProgress(targets, {}, 5), {});
    assert.deepEqual(setStatus(targets, {}, 'Dropped'), {});
    assert.deepEqual(setScore(targets, {}, 8), {});
});

test('sync buttons', () => {
    const at = (progress, jellyfin, extra = {}) => one({ progress, jellyfinProgress: jellyfin, ...extra });
    let buttons = syncButtons(at(5, 5), {}, { all: false });
    assert.deepEqual([buttons.up.enabled, buttons.up.reason, buttons.down.enabled, buttons.down.reason], [false, 'Up to date', false, 'Up to date']);
    buttons = syncButtons(at(3, 5), {}, { all: false });
    assert.deepEqual([buttons.up.enabled, buttons.up.label, buttons.down.reason], [true, 'Sync to AniList', 'Jellyfin is ahead. Use Sync to AniList']);
    buttons = syncButtons(at(7, 5), {}, { all: false });
    assert.deepEqual([buttons.up.enabled, buttons.up.reason, buttons.down.enabled], [false, 'AniList is ahead of Jellyfin. Use Sync to Jellyfin', true]);
    const behind = at(5, 5);
    buttons = syncButtons(behind, setProgress(behind, {}, 6), { all: false });
    assert.deepEqual([buttons.up.enabled, buttons.down.enabled, buttons.down.reason], [true, false, 'Sync your changes to AniList first']);
    const both = pair({ progress: 5, jellyfinProgress: 3 }, { progress: 7, jellyfinProgress: 3 });
    buttons = syncButtons(both, {}, { all: true });
    assert.deepEqual([buttons.up.enabled, buttons.up.label, buttons.up.reason, buttons.down.reason],
        [false, 'Sync to all trackers', 'Your trackers are ahead of Jellyfin. Use Sync to Jellyfin', 'Trackers differ. Open one tracker to sync from it']);
    buttons = syncButtons(both, setStatus(both, {}, 'Dropped'), { all: true });
    assert.deepEqual([buttons.up.enabled, buttons.down.reason], [true, 'Sync your changes to your trackers first']);
    assert.equal(syncButtons(one({ progress: 7, jellyfinProgress: 5 }, 'Point10', 'Mal'), {}, { all: false }).up.reason,
        'MyAnimeList is ahead of Jellyfin. Use Sync to Jellyfin');
});

test('sync up plan', () => {
    const targets = pair({ score: 70 }, { progress: 3, jellyfinProgress: 5 });
    const drafts = setProgress(targets.slice(0, 1), {}, 4);
    const plan = planSyncUp(targets, drafts, { isMovie: false, itemId: 'series' });
    assert.deepEqual(plan.syncs, ['Kitsu']);
    assert.equal(plan.edit.itemId, 'series');
    assert.deepEqual(plan.edit.edits, [{ provider: 'AniList', entryId: 1, alternativeId: null, shown: { status: 'Watching', progress: 3, score: 70 },
        status: 'Watching', progress: 4, score: null, clearScore: false }]);
});

test('a movie raises through an edit', () => {
    const targets = [target('Kitsu', { status: 'PlanToWatch', progress: 0, totalEpisodes: 1, jellyfinProgress: 1 }, 'Point10Half')];
    const plan = planSyncUp(targets, {}, { isMovie: true, itemId: 'movie' });
    assert.deepEqual(plan.syncs, []);
    assert.deepEqual([plan.edit.edits[0].status, plan.edit.edits[0].progress], ['Completed', 1]);
});

test('sync down plan', () => {
    const targets = pair({ progress: 7, jellyfinProgress: 3 }, { progress: 7, jellyfinProgress: 3 });
    assert.equal(planSyncDown(targets, {}), 'AniList');
    assert.equal(planSyncDown(targets, setStatus(targets, {}, 'Dropped')), null);
});
