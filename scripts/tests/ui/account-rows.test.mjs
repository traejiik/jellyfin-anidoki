import test from 'node:test';
import assert from 'node:assert/strict';
import * as common from '../../../jellyfin-anidoki/Configuration/CommonJs.js';

let focused = null;

class Node extends EventTarget {
    children = [];
    attributes = new Map();
    dataset = {};
    hidden = false;
    disabled = false;
    className = '';
    #text = '';
    constructor(tag) { super(); this.tag = tag; }
    get classList() {
        return { add: name => { this.className = `${this.className} ${name}`.trim(); }, contains: name => this.className.split(' ').includes(name) };
    }
    get textContent() { return this.#text + this.children.map(child => typeof child === 'string' ? child : child.textContent).join(''); }
    set textContent(value) { this.#text = String(value); this.children = []; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.#text = ''; this.children = children; }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) { this.attributes.delete(name); }
    focus() { focused = this; }
    click() { if (!this.disabled) this.dispatchEvent(new Event('click')); }
    *all() { for (const child of this.children) if (child instanceof Node) { yield child; yield* child.all(); } }
    find(predicate) { for (const node of this.all()) if (predicate(node)) return node; return null; }
    byClass(name) { return this.find(node => node.className.split(' ').includes(name)); }
    button(label) { return this.find(node => node.tag === 'button' && node.textContent === label); }
}

function setup(t) {
    const prior = Object.getOwnPropertyDescriptor(globalThis, 'document');
    Object.defineProperty(globalThis, 'document', { configurable: true, value: {
        createElement: tag => new Node(tag), createElementNS: (_, tag) => new Node(tag)
    } });
    globalThis.ApiClient = { getUrl: path => path };
    focused = null;
    t.after(() => { prior ? Object.defineProperty(globalThis, 'document', prior) : delete globalThis.document; delete globalThis.ApiClient; });
}

function render({ admin = false, summaries, action = async () => ({}), signal = new AbortController().signal } = {}) {
    const container = new Node('div');
    common.renderAccounts(container, { linked: ['AniList'], visible: ['AniList', 'Mal'], admin, userName: admin ? 'test' : 'your account', action, summaries }, signal);
    const [anilist, mal] = container.children;
    return { anilist, mal };
}

const key = name => Object.assign(new Event('keydown'), { key: name });

test('a linked row shows its name with Linked beside it, and no second line without a last update', t => {
    setup(t);
    const { anilist, mal } = render();
    const title = anilist.byClass('doki-row-title');
    assert.ok(title);
    assert.equal(title.textContent, 'AniListLinked');
    assert.equal(anilist.byClass('doki-row-name').children.length, 1);
    assert.equal(anilist.byClass('doki-status').textContent, '');
    assert.equal(mal.byClass('doki-row-title'), null);
    assert.equal(mal.byClass('doki-row-name').textContent, 'MyAnimeListNot linked');
});

test('a summary fills the second line and puts attention in the status line with an icon', t => {
    setup(t);
    const { anilist } = render({ summaries: { AniList: { lastUpdate: 'Last update 12 minutes ago', attention: 'AniList rejected your sign-in 2 hours ago.' }, Mal: { lastUpdate: 'ignored' } } });
    const name = anilist.byClass('doki-row-name');
    assert.equal(name.children[1].textContent, 'Last update 12 minutes ago');
    const line = anilist.byClass('doki-status');
    assert.equal(line.dataset.tone, 'attention');
    assert.equal(line.getAttribute('role'), 'status');
    assert.equal(line.children[0].tag, 'svg');
    assert.equal(line.textContent, 'AniList rejected your sign-in 2 hours ago.');
});

test('Disconnect opens the confirmation on the user page and moves focus to Keep linked', t => {
    setup(t);
    const { anilist } = render();
    const disconnect = anilist.button('Disconnect');
    const detail = anilist.byClass('doki-account-detail');
    assert.equal(disconnect.getAttribute('aria-expanded'), 'false');
    assert.equal(disconnect.getAttribute('aria-controls'), detail.id);
    disconnect.click();
    assert.equal(detail.hidden, false);
    assert.ok(detail.classList.contains('doki-confirm-inline'));
    assert.equal(detail.getAttribute('role'), 'group');
    assert.equal(detail.getAttribute('aria-label'), 'Confirm disconnecting AniList');
    assert.equal(detail.byClass('doki-confirm-text').textContent, 'Disconnect AniList? AniDōki stops updating it straight away; your list is not changed.');
    assert.equal(disconnect.getAttribute('aria-expanded'), 'true');
    assert.equal(focused, detail.button('Keep linked'));
    assert.ok(detail.button('Disconnect AniList').classList.contains('doki-button-danger'));
});

test('the admin wording names the user', t => {
    setup(t);
    const { anilist } = render({ admin: true });
    anilist.button('Disconnect').click();
    const detail = anilist.byClass('doki-account-detail');
    assert.equal(detail.getAttribute('aria-label'), 'Confirm disconnecting AniList for test');
    assert.equal(detail.byClass('doki-confirm-text').textContent, 'Disconnect AniList for test? AniDōki stops updating it straight away; test must sign in again to relink.');
});

for (const [how, close] of [
    ['Keep linked', detail => detail.button('Keep linked').click()],
    ['Escape', detail => detail.dispatchEvent(key('Escape'))],
    ['Disconnect again', (_, disconnect) => disconnect.click()]
]) {
    test(`${how} closes the confirmation and returns focus to Disconnect`, t => {
        setup(t);
        const { anilist } = render();
        const disconnect = anilist.button('Disconnect');
        const detail = anilist.byClass('doki-account-detail');
        disconnect.click();
        close(detail, disconnect);
        assert.equal(detail.hidden, true);
        assert.equal(detail.children.length, 0);
        assert.equal(detail.className, 'doki-account-detail');
        assert.equal(detail.getAttribute('role'), null);
        assert.equal(disconnect.getAttribute('aria-expanded'), 'false');
        assert.equal(focused, disconnect);
    });
}

test('other keys leave the confirmation open', t => {
    setup(t);
    const { anilist } = render();
    anilist.button('Disconnect').click();
    const detail = anilist.byClass('doki-account-detail');
    detail.dispatchEvent(key('Enter'));
    assert.equal(detail.hidden, false);
});

test('confirming disconnects once and disables the buttons while it runs', async t => {
    setup(t);
    const calls = [];
    let finish;
    const { anilist } = render({ action: (...args) => { calls.push(args); return new Promise(resolve => { finish = resolve; }); } });
    anilist.button('Disconnect').click();
    const confirm = anilist.button('Disconnect AniList');
    confirm.click();
    assert.deepEqual(calls, [['disconnect', 'AniList', {}]]);
    assert.equal(confirm.disabled, true);
    assert.equal(anilist.button('Keep linked').disabled, true);
    confirm.click();
    assert.equal(calls.length, 1);
    finish({ message: 'AniList disconnected.' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(confirm.disabled, false);
});

test('a failed disconnect keeps the confirmation open and shows the error', async t => {
    setup(t);
    const { anilist } = render({ action: async () => { throw new Error('Network down'); } });
    anilist.button('Disconnect').click();
    anilist.button('Disconnect AniList').click();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(anilist.byClass('doki-account-detail').hidden, false);
    assert.equal(anilist.byClass('doki-status').dataset.tone, 'error');
});

test('a hidden view ignores clicks and Escape', t => {
    setup(t);
    const lifetime = new AbortController();
    const { anilist } = render({ signal: lifetime.signal });
    lifetime.abort();
    anilist.button('Disconnect').click();
    assert.equal(anilist.byClass('doki-account-detail').hidden, true);
});
