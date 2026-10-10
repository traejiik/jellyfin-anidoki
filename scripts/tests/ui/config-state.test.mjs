import test from 'node:test';
import assert from 'node:assert/strict';
import { createDraft, changedValues, mergeChanges, mergePreferences, userPreferences, validateLibraries } from '../../../jellyfin-anidoki/Configuration/ConfigStateJs.js';

const saved = () => ({ callbackUrl: '', updateNsfw: true, ProviderApiAuth: [
    { Name: 'AniList', ClientId: 'one', ClientSecret: 'keep' },
    { Name: 'Mal', ClientId: 'two', ClientSecret: 'other' }
], UserConfig: [
    { UserId: 'u1', PlanToWatchOnly: true, RewatchCompleted: true, LibraryToCheck: [], UserApiAuth: [{ Name: 'AniList', AccessToken: 'old' }], KeyPairs: [{ Key: 'future', Value: 'keep' }] },
    { UserId: 'u2', PlanToWatchOnly: true, RewatchCompleted: false, LibraryToCheck: ['missing'] }
] });

test('draft is independent and excludes auth records and unknown settings', () => {
    const baseline = saved(); baseline.FutureOption = 'keep';
    const draft = createDraft(baseline);
    draft.ProviderApiAuth[0].ClientId = 'edited';
    assert.equal(baseline.ProviderApiAuth[0].ClientId, 'one');
    assert.equal(draft.UserConfig[0].UserApiAuth, undefined);
    assert.equal(draft.FutureOption, undefined);
});
test('no-op edits and presentation state do not make a draft dirty', () => {
    const baseline = createDraft(saved()); const draft = structuredClone(baseline);
    draft.openProvider = 'Mal'; draft.selectedUser = 'u2';
    assert.deepEqual(changedValues(baseline, draft), []);
});
test('a changed option preserves refreshed tokens, key pairs and unknown settings', () => {
    const baseline = saved(); const draft = createDraft(baseline); draft.updateNsfw = false;
    const latest = saved(); latest.UserConfig[0].UserApiAuth[0].AccessToken = 'new'; latest.FutureOption = 'retained';
    const result = mergeChanges(latest, baseline, draft);
    assert.deepEqual(result.conflicts, []);
    assert.equal(result.value.updateNsfw, false);
    assert.equal(result.value.UserConfig[0].UserApiAuth[0].AccessToken, 'new');
    assert.deepEqual(result.value.UserConfig[0].KeyPairs, latest.UserConfig[0].KeyPairs);
    assert.equal(result.value.FutureOption, 'retained');
});
test('two collapsed provider drafts save while untouched secrets stay intact', () => {
    const baseline = saved(); const draft = createDraft(baseline);
    draft.ProviderApiAuth[0].ClientId = 'edited1'; draft.ProviderApiAuth[1].ClientId = 'edited2';
    const result = mergeChanges(baseline, baseline, draft);
    assert.equal(result.value.ProviderApiAuth[0].ClientId, 'edited1');
    assert.equal(result.value.ProviderApiAuth[1].ClientId, 'edited2');
    assert.equal(result.value.ProviderApiAuth[0].ClientSecret, 'keep');
});
test('explicit secret clearing is saved', () => {
    const baseline = saved(); const draft = createDraft(baseline); draft.ProviderApiAuth[0].ClientSecret = '';
    assert.equal(mergeChanges(baseline, baseline, draft).value.ProviderApiAuth[0].ClientSecret, '');
});
test('two user drafts preserve other-user preferences and auth', () => {
    const baseline = saved(); const draft = createDraft(baseline);
    draft.UserConfig[0].PlanToWatchOnly = false; draft.UserConfig[1].RewatchCompleted = true;
    const result = mergeChanges(baseline, baseline, draft);
    assert.equal(result.value.UserConfig[0].PlanToWatchOnly, false);
    assert.equal(result.value.UserConfig[1].RewatchCompleted, true);
    assert.deepEqual(result.value.UserConfig[1].LibraryToCheck, ['missing']);
    assert.deepEqual(result.value.UserConfig[0].UserApiAuth, baseline.UserConfig[0].UserApiAuth);
});
test('discard copies confirmed values including false values', () => {
    const baseline = createDraft(saved()); const draft = structuredClone(baseline); draft.updateNsfw = false;
    const discarded = createDraft(baseline);
    assert.equal(discarded.updateNsfw, true);
    assert.equal(discarded.UserConfig[1].RewatchCompleted, false);
    assert.deepEqual(changedValues(baseline, discarded), []);
});
test('a remote edit to the same field names a conflict and keeps latest value', () => {
    const baseline = saved(); const draft = createDraft(baseline); draft.callbackUrl = 'https://mine';
    const latest = saved(); latest.callbackUrl = 'https://theirs';
    const result = mergeChanges(latest, baseline, draft);
    assert.deepEqual(result.conflicts, ['callbackUrl']);
    assert.equal(result.value.callbackUrl, 'https://theirs');
});
test('identical remotely applied draft is not a conflict', () => {
    const baseline = saved(); const draft = createDraft(baseline); draft.callbackUrl = 'https://same';
    const latest = saved(); latest.callbackUrl = 'https://same';
    assert.deepEqual(mergeChanges(latest, baseline, draft).conflicts, []);
});
test('provider conflicts and unrelated newly added providers are preserved', () => {
    const baseline = saved(); const draft = createDraft(baseline); draft.ProviderApiAuth[0].ClientId = 'mine';
    const latest = saved(); latest.ProviderApiAuth[0].ClientId = 'theirs'; latest.ProviderApiAuth.push({ Name: 'Simkl', ClientId: 'new', ClientSecret: 'fresh' });
    const result = mergeChanges(latest, baseline, draft);
    assert.deepEqual(result.conflicts, ['AniList.ClientId']);
    assert.deepEqual(result.value.ProviderApiAuth[2], latest.ProviderApiAuth[2]);
});
test('new user preference record uses effective defaults', () => {
    const baseline = { UserConfig: [{ UserId: 'new', ...userPreferences() }] };
    const draft = createDraft(baseline); draft.UserConfig[0].PlanToWatchOnly = false;
    const result = mergeChanges({}, baseline, draft);
    assert.deepEqual(result.conflicts, []);
    assert.equal(result.value.UserConfig[0].PlanToWatchOnly, false);
    assert.equal(result.value.UserConfig[0].RewatchCompleted, true);
});
test('selected mode requires IDs and unavailable IDs stay until explicitly removed', () => {
    assert.equal(validateLibraries('selected', []).valid, false);
    assert.deepEqual(validateLibraries('selected', ['missing']).ids, ['missing']);
    assert.deepEqual(validateLibraries('all', ['missing']).ids, []);
});
test('old user preferences default on but explicit false stays false', () => {
    assert.deepEqual(userPreferences(), { PlanToWatchOnly: true, RewatchCompleted: true, ShowLogNotifications: true, LibraryToCheck: [] });
    assert.equal(userPreferences({ ShowLogNotifications: false }).ShowLogNotifications, false);
});
test('preference save preserves a remotely changed untouched option', () => {
    const baseline = userPreferences(); const draft = { ...baseline, PlanToWatchOnly: false };
    const latest = { ...baseline, RewatchCompleted: false, ConnectedProviders: ['Kitsu'] };
    const result = mergePreferences(latest, baseline, draft);
    assert.deepEqual(result.conflicts, []);
    assert.equal(result.value.PlanToWatchOnly, false);
    assert.equal(result.value.RewatchCompleted, false);
    assert.equal(result.value.ConnectedProviders, undefined);
});
test('preference save names a remote library conflict', () => {
    const baseline = userPreferences(); const draft = { ...baseline, LibraryToCheck: ['mine'] };
    const latest = { ...baseline, LibraryToCheck: ['theirs'] };
    assert.deepEqual(mergePreferences(latest, baseline, draft).conflicts, ['LibraryToCheck']);
});
