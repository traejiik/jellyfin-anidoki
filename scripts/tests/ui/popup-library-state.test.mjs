import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';

const { libraryResults, libraryCard } = await importConfiguration('PopupLibraryStateJs.js');
const row = (extra = {}) => ({
    title: 'FLCL', isMovie: false, matchState: 'OnList', status: 'Watching',
    episodeCount: 6, progress: 8, totalEpisodes: 12, unloggedLibraryCount: 4, score: 85, ...extra
});

test('status counts follow the selected type and ignore the search', () => {
    const rows = [row(), row({ title: 'Mob Psycho 100', matchState: 'Unresolved', status: null }),
        row({ title: 'TRIGUN', matchState: 'NotOnList', status: null }), row({ isMovie: true })];
    const result = libraryResults({ rows }, { type: 'series', status: 'all', query: ' flcl ' });
    assert.deepEqual(result.rows, [rows[0]]);
    assert.equal(result.counts.all, 3);
    assert.equal(result.counts.Watching, 1);
    assert.equal(result.counts.NotOnList, 1);
    assert.deepEqual(libraryResults({ rows }, { type: 'series', status: 'all', query: '' }).rows, [rows[0], rows[2], rows[1]]);
    assert.deepEqual(libraryResults({ rows }, { type: 'series', status: 'NotOnList', query: '' }).rows, [rows[2]]);
});

test('card uses mapped remaining episodes and keeps zero, unknown and unrated distinct', () => {
    const now = Date.parse('2026-11-25T00:00Z');
    const card = libraryCard(row({ nextAiringAtUtc: '2026-11-27T01:00Z' }), now);
    assert.equal(card.progress, '8/12');
    assert.equal(card.solid, 8 / 12);
    assert.equal(card.tint, 4 / 12);
    assert.equal(card.countdown, '3 days');
    assert.equal(card.score, '8.5');
    const unknown = libraryCard(row({ progress: 0, totalEpisodes: 0, score: null, nextAiringAtUtc: '2026-11-24' }), now);
    assert.equal(unknown.progress, '0');
    assert.equal(unknown.solid, null);
    assert.equal(unknown.score, null);
    assert.equal(unknown.countdown, null);
    assert.equal(libraryCard(row({ matchState: 'Unresolved' }), now).status, 'Match unavailable');
    assert.equal(libraryCard(row({ isMovie: true, progress: 0, totalEpisodes: 1 }), now).progress, '0/1');
});
