import test from 'node:test';
import assert from 'node:assert/strict';
import { errorMessage, json, request, safeExternalUrl } from '../../../jellyfin-anidoki/Configuration/CommonJs.js';

test('JSON request errors show their message instead of serialized JSON', async () => {
    assert.equal(await errorMessage(Response.json('Check the address — then try again.', { status: 400 })), 'Check the address — then try again.');
    assert.equal(await errorMessage(Response.json({ title: 'Validation failed', errors: { User: ['Select a valid user.'] } }, { status: 400 })), 'Select a valid user.');
});

test('conflicts and validation errors retain their actionable message', async () => {
    assert.equal(await errorMessage(new Error('Changed on the server: callbackUrl')), 'Changed on the server: callbackUrl');
    assert.equal(await errorMessage(new Response('Pick a library', { status: 400 })), 'Pick a library');
});
test('authorization link rejects executable URLs and embedded credentials', () => {
    assert.throws(() => safeExternalUrl('javascript:alert(1)'));
    assert.throws(() => safeExternalUrl('https://user:password@example.test/'));
    assert.equal(safeExternalUrl('https://example.test/authorize?state=abc'), 'https://example.test/authorize?state=abc');
});
test('requests retain metacharacters in JSON bodies and forward abort signals', async t => {
    const calls = [];
    t.mock.method(globalThis, 'fetch', async (url, options) => {
        calls.push({ url, options });
        return new Response(JSON.stringify({ ConnectedProviders: ['Kitsu'] }), { headers: { 'Content-Type': 'application/json' } });
    });
    globalThis.ApiClient = { getUrl: path => `http://localhost:8096/base/${path}`, setRequestHeaders: headers => { headers.Authorization = 'test-session'; } };
    t.after(() => delete globalThis.ApiClient);
    const signal = new AbortController().signal;
    const body = { Username: 'a+b &/?', Password: 'fake&+?=#' };
    const result = await json('AniDoki/user/passwordGrant', { method: 'POST', body, signal });
    assert.deepEqual(result.ConnectedProviders, ['Kitsu']);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, 'http://localhost:8096/base/AniDoki/user/passwordGrant');
    assert.deepEqual(JSON.parse(calls[0].options.body), body);
    assert.equal(calls[0].options.signal, signal);
    assert.equal(calls[0].options.headers.Accept, 'application/json');
    assert.equal(calls[0].options.headers.Authorization, 'test-session');
    assert.equal(calls[0].options.credentials, 'same-origin');
});
test('failed mutation is reported without a retry', async t => {
    let calls = 0;
    t.mock.method(globalThis, 'fetch', async () => { calls++; return new Response('No access', { status: 403 }); });
    globalThis.ApiClient = { getUrl: path => path, setRequestHeaders: () => {} };
    t.after(() => delete globalThis.ApiClient);
    await assert.rejects(request('AniDoki/user/configuration', { method: 'PUT', body: {} }), response => response.status === 403);
    assert.equal(calls, 1);
});
