import test from 'node:test';
import assert from 'node:assert/strict';
import { allTrackers, buildSyncRequest, describeOperation, describeRequest, isFinished } from '../../../jellyfin-anidoki/Configuration/ManualSyncJs.js';

const form = () => ({ userId: 'u1', provider: 'AniList', trackers: 'All', syncAction: 'UpdateJellyfin', completed: true, watching: true });
test('both inbound statuses encode Both', () => assert.equal(buildSyncRequest(form(), ['AniList']).status, 'Both'));
test('one inbound status encodes Completed or Watching', () => {
    assert.equal(buildSyncRequest({ ...form(), watching: false }, ['AniList']).status, 'Completed');
    assert.equal(buildSyncRequest({ ...form(), completed: false }, ['AniList']).status, 'Watching');
});
test('neither inbound status cannot submit', () => assert.throws(() => buildSyncRequest({ ...form(), watching: false, completed: false }, ['AniList']), /status/i));
test('outbound sends All by default and ignores the inbound fields', () => {
    assert.deepEqual(buildSyncRequest({ ...form(), syncAction: 'UpdateProvider', provider: 'Mal', watching: false, completed: false }, ['Simkl', 'Annict']),
        { userId: 'u1', provider: allTrackers, syncAction: 'UpdateProvider', status: 'Both' });
});
test('outbound sends one chosen linked tracker', () => {
    assert.equal(buildSyncRequest({ ...form(), syncAction: 'UpdateProvider', trackers: 'Annict' }, ['Simkl', 'Annict']).provider, 'Annict');
});
test('outbound refuses a tracker that is not linked', () => {
    assert.throws(() => buildSyncRequest({ ...form(), syncAction: 'UpdateProvider', trackers: 'Mal' }, ['Simkl']), /linked tracker/i);
});
const names = key => ({ AniList: 'AniList', Mal: 'MyAnimeList' })[key] ?? key;
test('a run summary names the direction, user and trackers', () => {
    assert.equal(describeRequest({ syncAction: 'UpdateProvider', provider: allTrackers }, 'viewer', names), 'Jellyfin → Trackers · viewer · All linked trackers');
    assert.equal(describeRequest({ syncAction: 'UpdateProvider', provider: 'Mal' }, 'viewer', names), 'Jellyfin → Trackers · viewer · MyAnimeList');
    assert.equal(describeRequest({ syncAction: 'UpdateJellyfin', provider: 'AniList', status: 'Both' }, 'viewer', names), 'Tracker → Jellyfin · viewer · AniList · Both');
});
test('selected user and provider are captured independently of later edits', () => {
    const input = form(); const submitted = buildSyncRequest(input, ['AniList']); input.userId = 'u2'; input.provider = 'Mal';
    assert.equal(submitted.userId, 'u1'); assert.equal(submitted.provider, 'AniList');
});
test('unlinked provider and empty links cannot submit', () => {
    assert.throws(() => buildSyncRequest(form(), ['Mal']), /linked provider/i);
    assert.throws(() => buildSyncRequest({ ...form(), syncAction: 'UpdateProvider' }, []), /linked/i);
});
test('missing user and unknown direction cannot submit', () => {
    assert.throws(() => buildSyncRequest({ ...form(), userId: '' }, ['AniList']), /user/i);
    assert.throws(() => buildSyncRequest({ ...form(), syncAction: 'Preview' }, ['AniList']), /direction/i);
});
const view = changes => ({ kind: 'SyncToTrackers', state: 'Succeeded', done: 0, total: null, counts: {}, message: null, ...changes });
test('queued and running runs report progress', () => {
    assert.equal(describeOperation(view({ state: 'Queued' })).message, 'Waiting to start…');
    assert.equal(describeOperation(view({ state: 'Running', done: 3, total: 40 })).message, 'Running: 3 of 40 seasons checked…');
    assert.equal(describeOperation(view({ kind: 'SyncFromTrackers', state: 'Running', done: 2, total: 9 })).message, 'Running: 2 of 9 tracker entries checked…');
    assert.equal(describeOperation(view({ state: 'Running' })).message, 'Running…');
});
test('a finished run lists its non-zero counts and message', () => {
    const result = describeOperation(view({ state: 'Partial', counts: { Confirmed: 3, NoChange: 10, Failed: 1 }, message: 'Checked 14 seasons with watched episodes.' }));
    assert.equal(result.message, 'Finished with some problems. 3 updated · 10 unchanged · 1 failed. Checked 14 seasons with watched episodes.');
    assert.equal(result.tone, '');
});
test('results carry a tone', () => {
    assert.equal(describeOperation(view({ state: 'Succeeded' })).tone, 'success');
    assert.equal(describeOperation(view({ state: 'NoChange' })).tone, 'success');
    assert.equal(describeOperation(view({ state: 'Failed' })).tone, 'error');
    assert.equal(describeOperation(view({ state: 'Interrupted' })).tone, 'error');
    assert.equal(describeOperation(view({ state: 'Cancelled', message: 'Cancelled after 4 of 40 seasons. Updates already sent were kept.' })).message,
        'Cancelled. Cancelled after 4 of 40 seasons. Updates already sent were kept.');
});
test('only queued and running are unfinished', () => {
    assert.equal(isFinished('Queued'), false);
    assert.equal(isFinished('Running'), false);
    assert.equal(isFinished('Cancelled'), true);
});
