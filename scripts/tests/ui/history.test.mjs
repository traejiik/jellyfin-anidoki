import test from 'node:test';
import assert from 'node:assert/strict';
import * as common from '../../../jellyfin-anidoki/Web/CommonJs.js';
import { importConfiguration } from './load-configuration.mjs';
const { default: renderHistory } = await importConfiguration('HistoryJs.js');

class Node extends EventTarget {
    children = [];
    attributes = new Map();
    dataset = {};
    hidden = false;
    disabled = false;
    className = '';
    #text = '';
    constructor(tag) { super(); this.tag = tag; }
    get classList() { return { add: name => { this.className = `${this.className} ${name}`.trim(); } }; }
    get textContent() { return this.#text + this.children.map(child => typeof child === 'string' ? child : child.textContent).join(''); }
    set textContent(value) { this.#text = String(value); this.children = []; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.#text = ''; this.children = children; }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) { this.attributes.delete(name); }
    click() { if (!this.disabled) this.dispatchEvent(new Event('click')); }
    *all() { for (const child of this.children) if (child instanceof Node) { yield child; yield* child.all(); } }
    find(predicate) { for (const node of this.all()) if (predicate(node)) return node; return null; }
    findAll(predicate) { return [...this.all()].filter(predicate); }
    byClass(name) { return this.find(node => node.className.split(' ').includes(name)); }
    button(label) { return this.find(node => node.tag === 'button' && node.textContent === label); }
    byLabel(label) { return this.find(node => node.getAttribute?.('aria-label') === label); }
}

function setup(t) {
    Object.defineProperty(globalThis, 'document', { configurable: true, value: { createElement: tag => new Node(tag), createElementNS: (_, tag) => new Node(tag) } });
    t.after(() => { delete globalThis.document; });
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const entry = (episode, results = [{ provider: 'AniList', result: 'Updated' }]) =>
    ({ operationId: `op${episode}`, trigger: 'Playback', whenUtc: new Date().toISOString(), title: 'Frieren', episode, results });
const rows = container => container.byClass('doki-update-table').find(node => node.tag === 'tbody').children;

function render(load, options = {}) {
    const container = new Node('div');
    renderHistory(container, { load, ...options }, common, new AbortController().signal);
    return container;
}

test('while loading, three quiet rows hold the table, and screen readers hear it is loading', t => {
    setup(t);
    const container = render(() => new Promise(() => {}));
    assert.equal(rows(container).length, 3);
    assert.equal(container.byClass('doki-update-table').getAttribute('aria-busy'), 'true');
    assert.equal(container.byClass('doki-sr-only').textContent, 'Loading your history…');
    assert.equal(container.byClass('doki-update-table').find(node => node.tag === 'thead').textContent, 'EpisodeTriggerWhenResult', 'the header stays');
});

test('loaded rows show the episode, trigger, time and result lines', async t => {
    setup(t);
    const container = render(async () => ({ entries: [entry(12), entry(11)], more: false }));
    await settle();
    assert.equal(rows(container).length, 2);
    assert.equal(container.byClass('doki-update-table').getAttribute('aria-busy'), null);
    const first = rows(container)[0];
    assert.equal(first.byClass('doki-update-title').title, 'Frieren', 'a truncated title keeps the full title');
    assert.equal(first.byClass('doki-update-number').textContent, 'Episode 12');
    assert.equal(first.byClass('doki-update-trigger').textContent, 'Playback');
    assert.equal(first.byClass('doki-update-meta').textContent, 'Just now · Playback');
    assert.deepEqual(first.findAll(node => node.className === 'doki-update-line').map(line => [line.textContent, line.dataset.tone]), [['AniList updated', 'success']]);
    assert.equal(container.byClass('doki-pager').hidden, true, 'one page needs no pager');
});

test('pages of 10: Next replaces the rows with the next page, Previous goes back', async t => {
    setup(t);
    const calls = [];
    const container = render(async (offset, limit) => {
        calls.push([offset, limit]);
        return offset === 0 ? { entries: Array.from({ length: 10 }, (_, i) => entry(20 - i)), more: true, total: 11 } : { entries: [entry(10)], more: false, total: 11 };
    });
    await settle();
    const pager = container.byClass('doki-pager');
    assert.equal(pager.hidden, false);
    assert.equal(container.byLabel('Previous page').disabled, true);
    assert.equal(container.byClass('doki-pager-range').textContent, '1–10 of 11');
    container.byLabel('Next page').click(); await settle();
    assert.deepEqual(calls, [[0, 10], [10, 10]]);
    assert.equal(rows(container).length, 1, 'the page replaces the rows');
    assert.equal(container.byClass('doki-pager-range').textContent, '11–11 of 11');
    assert.equal(container.byLabel('Next page').disabled, true, 'nothing after this page');
    assert.equal(container.byClass('doki-sr-only').textContent, '11–11 of 11', 'screen readers hear the new page');
    container.byLabel('Previous page').click(); await settle();
    assert.deepEqual(calls.at(-1), [0, 10]);
    assert.equal(rows(container).length, 10);
});

test('the admin’s table adds a User column and names the user in the stacked line', async t => {
    setup(t);
    const container = render(async () => ({ entries: [{ ...entry(4), userId: 'u1' }], more: false }), { own: false, userName: id => id === 'u1' ? 'pr12-viewer' : '' });
    await settle();
    assert.equal(container.byClass('doki-update-table').find(node => node.tag === 'thead').textContent, 'EpisodeUserTriggerWhenResult');
    const first = rows(container)[0];
    assert.equal(first.byClass('doki-update-user').textContent, 'pr12-viewer');
    assert.equal(first.byClass('doki-update-meta').textContent, 'Just now · pr12-viewer · Playback');
});

test('the admin’s empty table speaks about users, across all five columns', async t => {
    setup(t);
    const container = render(async () => ({ entries: [], more: false }), { own: false, userName: () => '' });
    await settle();
    const message = container.byClass('doki-update-message');
    assert.equal(message.colSpan, 5);
    assert.match(message.textContent, /after a user watches an episode/);
});

test('an update that changed nothing reads No change needed', async t => {
    setup(t);
    const container = render(async () => ({ entries: [entry(3, [])], more: false }));
    await settle();
    assert.deepEqual(rows(container)[0].findAll(node => node.className === 'doki-update-line').map(line => [line.textContent, line.dataset.tone]), [['No change needed', 'muted']]);
});

test('nothing in the last 30 days says so in one spanning row', async t => {
    setup(t);
    const container = render(async () => ({ entries: [], more: false }));
    await settle();
    const message = container.byClass('doki-update-message');
    assert.equal(message.colSpan, 4);
    assert.equal(message.textContent, 'No updates in the last 30 days. They appear here after you watch an episode or mark one watched.');
    assert.equal(container.byClass('doki-pager').hidden, true);
});

test('a failed load offers Try again, which loads again', async t => {
    setup(t);
    let attempts = 0;
    const container = render(async () => { attempts++; if (attempts === 1) throw new Response('', { status: 500 }); return { entries: [entry(1)], more: false }; });
    await settle();
    const message = container.byClass('doki-update-message');
    assert.equal(message.dataset.tone, 'error');
    assert.match(message.textContent, /^Could not load your history\. Check your connection to Jellyfin, then try again\./);
    container.button('Try again').click(); await settle();
    assert.equal(attempts, 2);
    assert.equal(rows(container).length, 1);
});

test('unavailable storage says trackers are not being updated either, in the attention tone with the icon', async t => {
    setup(t);
    const container = render(async () => { throw Response.json({ storageUnavailable: true, message: 'locked' }, { status: 503 }); });
    await settle(); await settle();
    const message = container.byClass('doki-update-message');
    assert.equal(message.dataset.tone, 'attention');
    assert.ok(message.find(node => node.tag === 'svg'), 'the icon carries it as well as the colour');
    assert.match(message.textContent, /^History is unavailable because AniDōki could not open its storage\. Trackers are not being updated either\./);
    assert.equal(container.button('Try again'), null);
});
