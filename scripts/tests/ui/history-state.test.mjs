import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';
const { relativeTime, relativeLabel, lastUpdateCell, lastUpdateLine, attentionText, resultLine, historyRow, pageRange } = await importConfiguration('HistoryStateJs.js');

const now = Date.parse('2026-11-22T20:00:00Z');
const ago = ms => new Date(now - ms).toISOString();
const minutes = n => n * 60 * 1000, hours = n => minutes(60 * n), days = n => hours(24 * n);

test('relative times follow the design: just now, minutes, hours, yesterday, days', () => {
    assert.equal(relativeTime(ago(30 * 1000), now), 'just now');
    assert.equal(relativeTime(ago(minutes(1)), now), '1 minute ago');
    assert.equal(relativeTime(ago(minutes(12)), now), '12 minutes ago');
    assert.equal(relativeTime(ago(hours(1)), now), '1 hour ago');
    assert.equal(relativeTime(ago(hours(5)), now), '5 hours ago');
    assert.equal(relativeTime(ago(hours(30)), now), 'yesterday');
    assert.equal(relativeTime(ago(days(3)), now), '3 days ago');
    assert.equal(relativeTime(ago(days(29)), now), '29 days ago');
    assert.equal(relativeLabel(ago(hours(30)), now), 'Yesterday');
    assert.equal(relativeTime(new Date(now + minutes(5)).toISOString(), now), 'just now', 'a clock running ahead never reads as the future');
});

test('the Users table shows a time, nothing recent, or a dash when nothing is linked', () => {
    assert.equal(lastUpdateCell(ago(minutes(12)), true, now), '12 minutes ago');
    assert.equal(lastUpdateCell(null, true, now), 'No updates in the last 30 days');
    assert.equal(lastUpdateCell(ago(minutes(12)), false, now), '—');
});

test('a linked row reads Last update and a time, or nothing recent', () => {
    assert.equal(lastUpdateLine(ago(minutes(12)), now), 'Last update 12 minutes ago');
    assert.equal(lastUpdateLine(null, now), 'No updates in the last 30 days');
});

test('attention wording for every surface in the design', () => {
    const rejected = { kind: 'SignInRejected', sinceUtc: ago(hours(2)), step: 'unauthorized' };
    const busy = { kind: 'UpdatesFailing', sinceUtc: ago(hours(2)), step: 'rate-limited' };
    const unreachable = { ...busy, step: 'transient' };
    assert.equal(attentionText('AniList', rejected, { surface: 'table', now }), 'AniList needs a new sign-in');
    assert.equal(attentionText('AniList', busy, { surface: 'table', now }), 'AniList updates are failing');
    assert.equal(attentionText('AniList', rejected, { surface: 'user', now }), 'AniList rejected your sign-in 2 hours ago. Nothing is sent to AniList until you sign in again.');
    assert.equal(attentionText('AniList', rejected, { surface: 'admin', userName: 'test', now }), 'AniList rejected the sign-in 2 hours ago. Nothing is sent to AniList until test signs in again.');
    assert.equal(attentionText('Mal', busy, { surface: 'user', now }), 'Updates are failing: MyAnimeList was busy. Tried 4 times over 7 hours, then stopped.');
    assert.equal(attentionText('Mal', unreachable, { surface: 'admin', userName: 'test', now }), 'Updates are failing: MyAnimeList was unreachable. Tried 4 times over 7 hours, then stopped.');
    assert.equal(attentionText('AniList', null, { surface: 'user', now }), '');
});

test('a rejected Annict token names the token and who can replace it', () => {
    const rejected = { kind: 'SignInRejected', sinceUtc: ago(hours(30)), step: 'unauthorized' };
    assert.equal(attentionText('Annict', rejected, { surface: 'user', now }), 'Annict rejected your token yesterday. Nothing is sent to Annict until your administrator adds a new one.');
    assert.equal(attentionText('Annict', rejected, { surface: 'admin', userName: 'test', now }), 'Annict rejected test’s token yesterday. Nothing is sent to Annict until you add a new one.');
    assert.equal(attentionText('Annict', rejected, { surface: 'table', now }), 'Annict needs a new token');
});

test('history result wording and tones match the design', () => {
    const line = (result, extra = {}) => resultLine({ provider: 'AniList', result, ...extra });
    assert.deepEqual(line('Updated'), { text: 'AniList updated', tone: 'success' });
    assert.deepEqual(line('Updated', { try: 2 }), { text: 'AniList updated on the 2nd try', tone: 'success' });
    assert.equal(line('Updated', { try: 3 }).text, 'AniList updated on the 3rd try');
    assert.equal(line('Updated', { try: 4 }).text, 'AniList updated on the 4th try');
    assert.equal(line('Updated', { try: 5 }).text, 'AniList updated on the 5th try');
    assert.deepEqual(line('Retrying'), { text: 'AniList failed, retrying', tone: 'attention' });
    assert.deepEqual(line('GaveUp'), { text: 'AniList failed, gave up', tone: 'attention' });
    assert.deepEqual(line('NeedsSignIn'), { text: 'AniList needs a new sign-in', tone: 'attention' });
    assert.deepEqual(line('Unconfirmed'), { text: 'AniList may have updated; check it', tone: 'attention' });
    assert.deepEqual(line('Skipped', { step: 'plan-to-watch-only' }), { text: 'AniList skipped: not on Plan to Watch', tone: 'muted' });
    assert.deepEqual(line('Skipped', { step: 'something-new' }), { text: 'AniList skipped', tone: 'muted' });
    assert.deepEqual(line('Unresolved'), { text: 'AniList: no matching entry', tone: 'muted' });
    assert.deepEqual(line('Failed'), { text: 'AniList failed', tone: 'attention' });
});

test('a history row names the episode, trigger and time, and says No change needed when nothing changed', () => {
    const row = historyRow({ operationId: 'op', trigger: 'WatchedToggle', whenUtc: ago(minutes(12)), title: 'Frieren', episode: 12, results: [] }, now);
    assert.deepEqual(row, { id: 'op', title: 'Frieren', episode: 'Episode 12', trigger: 'Marked watched', when: '12 minutes ago', lines: [{ text: 'No change needed', tone: 'muted' }] });
    const movie = historyRow({ operationId: 'm', trigger: 'Playback', whenUtc: ago(days(2)), title: 'Your Name', episode: null, results: [{ provider: 'Kitsu', result: 'Updated' }] }, now);
    assert.equal(movie.episode, '');
    assert.equal(movie.trigger, 'Playback');
    assert.deepEqual(movie.lines, [{ text: 'Kitsu updated', tone: 'success' }]);
    assert.equal(historyRow({ trigger: 'ConfirmedManual', whenUtc: ago(0), title: 'x', results: [] }, now).trigger, 'Confirmed mark');
    assert.equal(historyRow({ trigger: 'Retry', whenUtc: ago(0), title: 'x', results: [] }, now).trigger, 'Retry');
});

test('the pager names the rows shown', () => {
    assert.equal(pageRange(0, 10), 'Showing 1–10');
    assert.equal(pageRange(10, 3), 'Showing 11–13');
    assert.equal(pageRange(0, 0), '');
});
