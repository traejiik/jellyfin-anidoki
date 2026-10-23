import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';
const { restorePluginPagesEntry, guardPluginPagesEntry } = await importConfiguration('PluginPagesGuardJs.js');

// Menus are parents holding an anchor and, once Plugin Pages has run, a marker beside it.
function fakeDocument({ accountMenu = false, drawer = false, markerIn = [] } = {}) {
    const removed = [];
    const menu = name => {
        const marker = markerIn.includes(name) ? { name: `${name} marker`, remove: () => removed.push(`${name} marker`) } : null;
        const parent = { querySelector: selector => selector === ':scope > .pluginMenuOptions' && !removed.includes(`${name} marker`) ? marker : null };
        return { anchor: { parentElement: parent }, marker };
    };
    const account = accountMenu ? menu('account') : null, legacy = drawer ? menu('drawer') : null;
    return {
        removed,
        body: {},
        querySelector: selector => selector === "[href='#/mypreferencesmenu']" ? account?.anchor ?? null
            : selector === '.mainDrawer-scrollContainer > .userMenuOptions' ? legacy?.anchor ?? null : null,
        querySelectorAll: () => [account?.marker, legacy?.marker].filter(Boolean)
    };
}
const pluginPages = (initialized = true) => {
    const calls = [];
    return { calls, initialized, onReady: () => calls.push('onReady'), populateSidebar: () => calls.push('populateSidebar') };
};

test('replays Plugin Pages setup when the rebuilt account menu lost its entry', () => {
    const pages = pluginPages();
    assert.equal(restorePluginPagesEntry(fakeDocument({ accountMenu: true }), pages), true);
    assert.deepEqual(pages.calls, ['onReady', 'populateSidebar']);
});

test('falls back to the legacy drawer when there is no account menu', () => {
    const pages = pluginPages();
    assert.equal(restorePluginPagesEntry(fakeDocument({ drawer: true }), pages), true);
});

test('moves an entry left in the drawer back to the account menu once it remounts', () => {
    const pages = pluginPages();
    const doc = fakeDocument({ accountMenu: true, drawer: true, markerIn: ['drawer'] });
    assert.equal(restorePluginPagesEntry(doc, pages), true);
    assert.deepEqual(doc.removed, ['drawer marker']);
    assert.deepEqual(pages.calls, ['onReady', 'populateSidebar']);
});

test('leaves Plugin Pages alone while its entry is in place, before it starts, or without a menu', () => {
    const present = pluginPages();
    assert.equal(restorePluginPagesEntry(fakeDocument({ accountMenu: true, drawer: true, markerIn: ['account'] }), present), false);
    const starting = pluginPages(false);
    assert.equal(restorePluginPagesEntry(fakeDocument({ accountMenu: true }), starting), false);
    const noMenu = pluginPages();
    assert.equal(restorePluginPagesEntry(fakeDocument(), noMenu), false);
    assert.equal(restorePluginPagesEntry(fakeDocument({ accountMenu: true }), undefined), false);
    assert.deepEqual([...present.calls, ...starting.calls, ...noMenu.calls], []);
});

test('coalesces a burst of mutations into one check and replaces an earlier guard', () => {
    const observers = [], timers = [];
    class FakeObserver {
        constructor(callback) { this.callback = callback; this.connected = false; observers.push(this); }
        observe() { this.connected = true; }
        disconnect() { this.connected = false; }
    }
    const win = { MutationObserver: FakeObserver, setTimeout: callback => timers.push(callback) };
    const pages = pluginPages();
    const doc = fakeDocument({ accountMenu: true });
    guardPluginPagesEntry(win, doc, () => pages);
    const guard = guardPluginPagesEntry(win, doc, () => pages);
    assert.equal(observers[0].connected, false);
    assert.equal(guard.connected, true);
    guard.callback(); guard.callback(); guard.callback();
    assert.equal(timers.length, 1);
    timers[0]();
    assert.deepEqual(pages.calls, ['onReady', 'populateSidebar']);
    guard.callback();
    assert.equal(timers.length, 2);
});
