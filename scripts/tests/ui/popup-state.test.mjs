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
