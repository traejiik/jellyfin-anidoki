import test from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as nextTurn } from 'node:timers/promises';
import adminController from '../../../jellyfin-anidoki/Configuration/ConfigPageJs.js';

class DomElement {
    constructor(tag = 'div') {
        this.tag = tag;
        this.children = [];
        this.classList = { add() {} };
        this.listeners = new Map();
        this.dataset = {};
        this.style = { setProperty() {} };
        this.value = '';
    }

    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    addEventListener(type, listener, { signal } = {}) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push({ listener, signal });
        this.listeners.set(type, listeners);
    }
    emit(type, event = {}) {
        for (const { listener, signal } of this.listeners.get(type) ?? []) {
            if (!signal?.aborted) listener(event);
        }
    }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    focus() { DomElement.focused = this; }
    scrollIntoView() {}
    querySelectorAll(selector) {
        return this.children.flatMap(child => [ ...(child.tag === selector ? [child] : []), ...child.querySelectorAll(selector) ]);
    }
}

async function waitFor(predicate) {
    const deadline = Date.now() + 1000;
    while (!predicate()) {
        assert.ok(Date.now() < deadline, 'Controller did not reach the expected state');
        await nextTurn();
    }
}

function setGlobal(t, name, value) {
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
    t.after(() => {
        if (descriptor) Object.defineProperty(globalThis, name, descriptor);
        else delete globalThis[name];
    });
}

async function setupAdmin(t, users = []) {
    const ids = ['TemplateConfigForm', 'configurationSummary', 'providerCards', 'userRows', 'userPanel',
        'saveStatus', 'refreshAccounts', 'useCurrent', 'useLocal', 'copyCallback', 'generalCallbackUrlInput',
        'addressCheck', 'addressStatus', 'testAnimeListSaveLocation', 'folderStatus', 'discardChanges',
        'apiUrl', 'saveChanges', 'navigationTabs', 'accountsRefreshStatus', 'accountsStatus',
        'closeUserPanel', 'selectedUserName', 'userAccounts', 'userPreferences', 'userLibraries'];
    const nodes = Object.fromEntries(ids.map(id => [id, new DomElement()]));
    nodes.apiUrl.type = 'url';
    nodes.apiUrl.dataset.field = 'callbackUrl';
    nodes.TemplateConfigForm.hidden = true;
    nodes.userPanel.hidden = true;
    nodes.accountsRefreshStatus.hidden = true;
    const root = new DomElement(), view = new DomElement(), bar = new DomElement(), pageStatus = new DomElement();
    view.querySelector = selector => selector === '.anidoki' ? root : selector === '.ad-save-bar' ? bar
        : selector === '[data-page-status]' ? pageStatus : nodes[selector.slice(1)];
    view.querySelectorAll = selector => selector === '[data-field]' ? [nodes.apiUrl] : Object.values(nodes);
    root.querySelector = selector => view.querySelector(selector);
    root.querySelectorAll = () => [];
    setGlobal(t, 'document', { createElement: tag => new DomElement(tag), createElementNS: (_, tag) => new DomElement(tag), querySelector: () => ({}), addEventListener() {} });
    setGlobal(t, 'window', { addEventListener() {} });
    setGlobal(t, 'ResizeObserver', class { observe() {} disconnect() {} });
    setGlobal(t, 'IntersectionObserver', class { observe() {} disconnect() {} });
    setGlobal(t, 'ApiClient', {
        getUrl: path => path === 'AniDoki/assets/common.js'
            ? new URL('../../../jellyfin-anidoki/Configuration/CommonJs.js', import.meta.url).href
            : path === 'AniDoki/assets/config-state.js'
                ? new URL('../../../jellyfin-anidoki/Configuration/ConfigStateJs.js', import.meta.url).href
                : `https://jellyfin.example.test/${path}`,
        getPluginConfiguration: async () => ({ callbackUrl: '', ProviderApiAuth: [], UserConfig: [] }),
        getUsers: async () => users,
        setRequestHeaders() {}
    });
    let stream;
    const failedResponse = new Response(new ReadableStream({ start(controller) { stream = controller; } }), { status: 400 });
    t.mock.method(globalThis, 'fetch', async url => {
        if (url.endsWith('/parameters')) return Response.json({ libraries: [] });
        const address = new URL(url).searchParams.get('address');
        return address ? Response.json({ baseAddress: address, callbackUrl: `${address}/AniDoki/authCallback` }) : failedResponse;
    });
    const timers = new Map();
    let timerId = 0;
    t.mock.method(globalThis, 'setTimeout', callback => { timers.set(++timerId, callback); return timerId; });
    t.mock.method(globalThis, 'clearTimeout', id => timers.delete(id));
    adminController(view);
    view.emit('viewshow');
    t.after(() => view.emit('viewhide'));
    await waitFor(() => !nodes.TemplateConfigForm.hidden);
    return {
        view, nodes, failedResponse, pageStatus,
        runPreview() {
            const [id, callback] = [...timers].at(-1);
            timers.delete(id);
            return callback();
        },
        editAddress(value) { nodes.apiUrl.value = value; root.emit('input', { target: nodes.apiUrl }); },
        finishError() { stream.enqueue(new TextEncoder().encode('An older address was invalid.')); stream.close(); }
    };
}

test('a deferred old callback error cannot replace a newer valid preview', async t => {
    const page = await setupAdmin(t);
    const oldPreview = page.runPreview();
    await waitFor(() => page.failedResponse.body.locked);
    page.editAddress('https://newer.example.test/base');
    await page.runPreview();
    assert.equal(page.nodes.addressStatus.textContent, 'Callback preview ready.');
    page.finishError();
    await oldPreview;
    assert.equal(page.nodes.addressStatus.textContent, 'Callback preview ready.');
    assert.equal(page.nodes.apiUrl['aria-invalid'], 'false');
    assert.equal(page.nodes.generalCallbackUrlInput.value, 'https://newer.example.test/base/AniDoki/authCallback');
});

test('a deferred callback error does not update a hidden view', async t => {
    const page = await setupAdmin(t);
    const pendingPreview = page.runPreview();
    await waitFor(() => page.failedResponse.body.locked);
    page.view.emit('viewhide');
    const text = page.nodes.addressStatus.textContent;
    const invalid = page.nodes.apiUrl['aria-invalid'];
    page.finishError();
    await pendingPreview;
    assert.equal(page.nodes.addressStatus.textContent, text);
    assert.equal(page.nodes.apiUrl['aria-invalid'], invalid);
});

test('refresh success waits for the actual response and retains an unsaved address', async t => {
    const page = await setupAdmin(t);
    page.editAddress('https://draft.example.test');
    let finishRefresh;
    t.mock.method(globalThis.ApiClient, 'getPluginConfiguration', () => new Promise(resolve => { finishRefresh = resolve; }));
    page.nodes.refreshAccounts.emit('click');
    assert.equal(page.nodes.accountsRefreshStatus.hidden, true);
    assert.equal(page.nodes.refreshAccounts.disabled, true);
    finishRefresh({ callbackUrl: '', ProviderApiAuth: [], UserConfig: [] });
    await waitFor(() => !page.nodes.refreshAccounts.disabled);
    assert.equal(page.nodes.accountsRefreshStatus.hidden, false);
    assert.equal(page.nodes.apiUrl.value, 'https://draft.example.test');
    assert.equal(page.pageStatus.textContent, '');
});

test('a failed new refresh clears the old success and reports the error in the card', async t => {
    const page = await setupAdmin(t);
    page.nodes.refreshAccounts.emit('click');
    await waitFor(() => !page.nodes.refreshAccounts.disabled);
    assert.equal(page.nodes.accountsRefreshStatus.hidden, false);
    t.mock.method(globalThis.ApiClient, 'getPluginConfiguration', async () => { throw new Error('Refresh unavailable'); });
    page.nodes.refreshAccounts.emit('click');
    await waitFor(() => page.nodes.accountsStatus.textContent === 'Refresh unavailable');
    assert.equal(page.nodes.accountsRefreshStatus.hidden, true);
    assert.equal(page.nodes.accountsStatus.dataset.tone, 'error');
    assert.equal(page.pageStatus.textContent, '');
});


test('closing and reopening the user editor retains drafts and restores focus', async t => {
    const page = await setupAdmin(t, [{ Id: 'u1', Name: 'test user' }]);
    const edit = () => page.nodes.userRows.children[0].children[2].children[0];
    edit().emit('click');
    const preference = page.nodes.userPreferences.children[0].children[0];
    preference.checked = false;
    preference.emit('change');
    page.nodes.closeUserPanel.emit('click');
    assert.equal(page.nodes.userPanel.hidden, true);
    assert.equal(DomElement.focused, edit());

    page.nodes.refreshAccounts.emit('click');
    await waitFor(() => !page.nodes.accountsRefreshStatus.hidden);
    assert.equal(page.nodes.userPanel.hidden, true, 'Refreshing must not reopen a closed editor');
    edit().emit('click');
    assert.equal(page.nodes.userPreferences.children[0].children[0].checked, false);
    assert.equal(DomElement.focused, page.nodes.selectedUserName);
});
