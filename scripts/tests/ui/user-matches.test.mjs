import test from 'node:test';
import assert from 'node:assert/strict';
import { openMatchesFromSettingsLink } from '../../../jellyfin-anidoki/Web/ConfigPageUserJs.js';
import { createDocument } from './fake-dom.mjs';
import startUserMatches, { guardSettingsForm } from '../../../jellyfin-anidoki/Web/UserMatchesJs.js';

class Section {
    constructor() { this.listeners = []; }
    addEventListener(type, handler, { signal } = {}) {
        if (signal?.aborted) return;
        const entry = { type, handler };
        this.listeners.push(entry);
        signal?.addEventListener('abort', () => { this.listeners = this.listeners.filter(item => item !== entry); }, { once: true });
    }
    dispatch(type, event) {
        const record = { prevented: false, stopped: false, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...event };
        for (const entry of this.listeners.filter(item => item.type === type)) entry.handler(record);
        return record;
    }
}
const field = (tagName, inPicker = false) => ({ tagName, closest: selector => inPicker && selector.includes('#entryPicker') ? {} : null });

test('a submit inside the Matches section never reaches the settings form', () => {
    const section = new Section();
    guardSettingsForm(section, new AbortController().signal);
    const submitted = section.dispatch('submit', { target: {} });
    assert.equal(submitted.stopped, true);
    assert.equal(submitted.prevented, true);
});

test('Enter in a match field does not submit the settings form, but the picker and the note keep theirs', () => {
    const section = new Section();
    guardSettingsForm(section, new AbortController().signal);
    assert.equal(section.dispatch('keydown', { key: 'Enter', target: field('INPUT') }).prevented, true);
    assert.equal(section.dispatch('keydown', { key: 'Enter', target: field('INPUT', true) }).prevented, false, 'Enter searches in the picker');
    assert.equal(section.dispatch('keydown', { key: 'Enter', target: field('TEXTAREA') }).prevented, false, 'the note takes new lines');
    assert.equal(section.dispatch('keydown', { key: 'a', target: field('INPUT') }).prevented, false);
});

test('showing the page again replaces the guard instead of adding to it', () => {
    const section = new Section();
    let lifetime;
    for (let shown = 0; shown < 3; shown++) {
        lifetime?.abort();
        lifetime = new AbortController();
        guardSettingsForm(section, lifetime.signal);
    }
    assert.equal(section.listeners.length, 2);
    lifetime.abort();
    assert.equal(section.listeners.length, 0);
});

test('the Settings shortcut opens Advanced and the existing match dialog once even if the router strips its marker', () => {
    const key = 'anidoki.openTrackerMatches', clicks = [];
    const items = new Map([[key, JSON.stringify({ sourceKey: 'season:flcl:1', expires: Date.now() + 60_000 })]]);
    const win = {
        location: { href: 'http://jf/web/#/userpluginsettings?pageUrl=/AniDoki/settings' },
        sessionStorage: { getItem: name => items.get(name), removeItem: name => items.delete(name) }
    };
    const view = { querySelector: selector => ({ click: () => clicks.push(selector) }) };
    openMatchesFromSettingsLink(view, key, source => clicks.push(source), win);
    openMatchesFromSettingsLink(view, key, source => clicks.push(source), win);
    assert.deepEqual(clicks, ['[data-section="#doki-advanced"]', 'season:flcl:1']);
});

test('a direct link consumes its Matches marker without losing the Plugin Pages destination', () => {
    const href = 'http://jf/web/#/userpluginsettings.html?pageUrl=/AniDoki/settings&dokiSection=trackerMatchesTitle&dokiSource=movie%3Aexample';
    const clicks = [];
    const win = {
        location: { href },
        history: { replaceState: (_state, _title, resultingHref) => { win.location.href = resultingHref; } }
    };
    const view = { querySelector: selector => ({ click: () => clicks.push(selector) }) };
    openMatchesFromSettingsLink(view, 'unused', source => clicks.push(source), win);
    openMatchesFromSettingsLink(view, 'unused', source => clicks.push(source), win);
    assert.equal(new URLSearchParams(new URL(win.location.href).hash.split('?')[1]).get('pageUrl'), '/AniDoki/settings');
    assert.deepEqual(clicks, ['[data-section="#doki-advanced"]', 'movie:example']);
});

test('the show shortcut initializes the episode map, correction, breadcrumbs and close actions', async () => {
    const doc = createDocument(), nodes = new Map();
    const get = selector => {
        if (!nodes.has(selector)) nodes.set(selector, doc.createElement('div'));
        return nodes.get(selector);
    };
    const section = doc.createElement('section');
    section.querySelector = get;
    const dialog = get('#userMatchDialog');
    dialog.open = false;
    dialog.close = () => { dialog.open = false; };
    const views = ['list', 'detail', 'editor'].map(name => {
        const view = get(`view:${name}`);
        view.dataset.view = name;
        dialog.append(view);
        return view;
    });
    for (const id of ['#userEpisodeRows', '#userDraftRows', '#userSourceRows']) {
        const table = doc.createElement('div');
        table.className = 'doki-table-shell';
        table.append(get(id));
    }

    const state = new URL('../../../jellyfin-anidoki/Web/MatchEditorStateJs.js', import.meta.url).href;
    const editor = 'data:text/javascript,' + encodeURIComponent(`
        export const createEntryPicker = () => ({ close() {} });
        export const bindEditor = () => {};
        export const bindDialog = () => {};
        export const bindPageActions = (node, actions) => { node.actions = actions; };
        export const renderSegmented = () => {};
        export const renderEpisodeMap = rows => { rows.textContent = 'map loaded'; };
        export const renderSourceRows = () => {};
        export const renderEditor = root => { root.textContent = 'editor loaded'; };
        export const renderIssues = () => {};
    `);
    const title = 'FLCL · season 1';
    const sourceKey = 'season:flcl:1';
    const common = {
        assetUrl: asset => asset === 'match-editor.js' ? editor : state,
        showThemedDialog: node => { node.open = true; },
        element: (tag, className, label) => {
            const node = doc.createElement(tag);
            node.className = className ?? '';
            node.textContent = label ?? '';
            return node;
        },
        icon: () => doc.createElement('span'),
        providerName: name => name,
        providers: [{ key: 'Kitsu' }],
        status: (node, label) => { node.textContent = label; },
        json: async path => {
            if (path.includes('/sources?')) return { total: 0, items: [] };
            if (path.endsWith('/preview')) return { rows: [] };
            return { title, sourceKey, episodes: [1], providers: [{
                provider: 'Kitsu', effective: 'Shared', shared: { mapping: { ranges: [], exceptions: [] } }
            }] };
        }
    };
    const open = await startUserMatches(section, common, {}, new AbortController().signal);
    await open(sourceKey);
    assert.equal(get('#userDialogTitle').textContent, title);
    assert.equal(get('#userEpisodeRows').textContent, 'map loaded');
    section.actions.correct();
    assert.equal(get('#matchEditor').textContent, 'editor loaded');
    assert.deepEqual(views.map(view => view.hidden), [true, true, false]);
    section.actions['back-to-season']();
    assert.deepEqual(views.map(view => view.hidden), [true, false, true]);
    section.actions['back-to-list']();
    assert.deepEqual(views.map(view => view.hidden), [false, true, true]);
    section.actions['close-dialog']();
    assert.equal(dialog.open, false);
});
