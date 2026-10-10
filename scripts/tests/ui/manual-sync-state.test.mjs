import test from 'node:test';
import assert from 'node:assert/strict';
import { buildSyncRequest, syncFeedback } from '../../../jellyfin-anidoki/Configuration/ManualSyncJs.js';

const form = () => ({ userId: 'u1', provider: 'AniList', syncAction: 'UpdateJellyfin', completed: true, watching: true });
test('both inbound statuses encode Both', () => assert.equal(buildSyncRequest(form(), ['AniList']).status, 'Both'));
test('one inbound status encodes Completed or Watching', () => {
    assert.equal(buildSyncRequest({ ...form(), watching: false }, ['AniList']).status, 'Completed');
    assert.equal(buildSyncRequest({ ...form(), completed: false }, ['AniList']).status, 'Watching');
});
test('neither inbound status cannot submit', () => assert.throws(() => buildSyncRequest({ ...form(), watching: false, completed: false }, ['AniList']), /status/i));
test('outbound ignores displayed provider/status and sends compatibility values', () => {
    assert.deepEqual(buildSyncRequest({ ...form(), syncAction: 'UpdateProvider', provider: 'Mal', watching: false, completed: false }, ['Simkl', 'Annict']), { userId: 'u1', provider: 'Simkl', syncAction: 'UpdateProvider', status: 'Both' });
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
test('response feedback distinguishes request completion from confirmed tracker writes', () => {
    assert.match(syncFeedback(true), /request finished.*logs/i);
    assert.match(syncFeedback(false), /could not confirm.*before running again/i);
});
