import test from 'node:test';
import assert from 'node:assert/strict';
import * as manualSync from '../../../jellyfin-anidoki/Configuration/ManualSyncJs.js';

class Link {
    constructor(href, selected = false, current = null) {
        this.attributes = new Map([['href', href]]);
        if (current !== null) this.attributes.set('aria-current', current);
        this.classes = new Set(selected ? ['Mui-selected'] : []);
        this.classList = { contains: name => this.classes.has(name), toggle: (name, on) => on ? this.classes.add(name) : this.classes.delete(name) };
    }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
}

function setup(t, { pluginsCurrent = null } = {}) {
    const observers = [];
    const oldObserver = globalThis.MutationObserver;
    globalThis.MutationObserver = class {
        constructor(callback) { this.callback = callback; this.active = false; observers.push(this); }
        observe() { this.active = true; }
        disconnect() { this.active = false; }
    };
    t.after(() => { if (oldObserver) globalThis.MutationObserver = oldObserver; else delete globalThis.MutationObserver; });
    const primary = new Link('#/configurationpage?name=AniDoki');
    const plugins = new Link('#/dashboard/plugins', true, pluginsCurrent);
    const unrelated = new Link('#/configurationpage?name=OtherPlugin', false, 'step');
    const sidebar = {
        querySelector: selector => selector.includes('AniDoki') ? primary : plugins,
        querySelectorAll: () => [primary, plugins, unrelated]
    };
    const document = { body: {}, querySelectorAll: () => [sidebar], defaultView: { location: { hash: '#/configurationpage?name=AniDoki_ManualSync' } } };
    const lifetime = new AbortController();
    assert.equal(typeof manualSync.selectManualSyncSidebar, 'function', 'manual sync must provide its scoped sidebar correction');
    manualSync.selectManualSyncSidebar(lifetime.signal, document);
    const rerender = () => observers.filter(observer => observer.active).forEach(observer => observer.callback([]));
    return { primary, plugins, unrelated, document, lifetime, observers, rerender };
}

test('manual sync selects the existing AniDōki link and clears generic Plugins', t => {
    const page = setup(t);
    assert.equal(page.primary.classList.contains('Mui-selected'), true);
    assert.equal(page.primary.getAttribute('aria-current'), 'page');
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.getAttribute('aria-current'), null);
    assert.equal(page.unrelated.getAttribute('aria-current'), 'step');
    page.lifetime.abort();
});

test('host rerenders are corrected only while manual sync remains visible', t => {
    const page = setup(t);
    page.primary.classList.toggle('Mui-selected', false);
    page.plugins.classList.toggle('Mui-selected', true);
    page.rerender();
    assert.equal(page.primary.classList.contains('Mui-selected'), true);
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.primary.getAttribute('aria-current'), null);
    assert.equal(page.plugins.classList.contains('Mui-selected'), true);
    assert.ok(page.observers.every(observer => !observer.active));
    page.primary.classList.toggle('Mui-selected', false);
    page.rerender();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
});

test('a fresh lifetime can reopen without keeping the old correction alive', t => {
    const page = setup(t);
    page.lifetime.abort();
    const fresh = new AbortController();
    manualSync.selectManualSyncSidebar(fresh.signal, page.document);
    assert.equal(page.primary.classList.contains('Mui-selected'), true);
    assert.equal(page.observers.filter(observer => observer.active).length, 2);
    fresh.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.primary.getAttribute('aria-current'), null);
    assert.ok(page.observers.every(observer => !observer.active));
});

test('departing to Dashboard does not restore stale Plugins selection', t => {
    const page = setup(t);
    page.document.defaultView.location.hash = '#/dashboard';
    page.primary.classList.toggle('Mui-selected', false);
    page.plugins.classList.toggle('Mui-selected', false);
    page.rerender();
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    assert.equal(page.primary.getAttribute('aria-current'), null);
});

test('departing to Dashboard does not restore a stale Plugins ARIA current marker', t => {
    const page = setup(t, { pluginsCurrent: 'page' });
    assert.equal(page.plugins.getAttribute('aria-current'), null);
    page.document.defaultView.location.hash = '#/dashboard';
    page.lifetime.abort();
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.getAttribute('aria-current'), null);
});

test('departing to Settings preserves the host selection and removes owned ARIA', t => {
    const page = setup(t);
    page.document.defaultView.location.hash = '#/configurationpage?name=AniDoki';
    page.plugins.classList.toggle('Mui-selected', false);
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), true);
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    assert.equal(page.primary.getAttribute('aria-current'), null);
});

test('departing to Plugins restores its selection without keeping AniDōki selected', t => {
    const page = setup(t);
    page.document.defaultView.location.hash = '#/dashboard/plugins';
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.classList.contains('Mui-selected'), true);
});

test('departing to another hidden plugin page leaves generic Plugins selected', t => {
    const page = setup(t);
    page.document.defaultView.location.hash = '#/configurationpage?name=OtherHiddenPage';
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.classList.contains('Mui-selected'), true);
});

test('departing to another main-menu plugin leaves its host selection alone', t => {
    const page = setup(t);
    page.document.defaultView.location.hash = '#/configurationpage?name=OtherPlugin';
    page.unrelated.classList.toggle('Mui-selected', true);
    page.lifetime.abort();
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.equal(page.plugins.classList.contains('Mui-selected'), false);
    assert.equal(page.unrelated.classList.contains('Mui-selected'), true);
    assert.equal(page.unrelated.getAttribute('aria-current'), 'step');
});

test('the correction follows a sidebar remount and restores both instances on hide', t => {
    const page = setup(t);
    const primary = new Link('#/configurationpage?name=AniDoki');
    const plugins = new Link('#/dashboard/plugins', true);
    page.document.querySelectorAll = () => [{
        querySelector: selector => selector.includes('AniDoki') ? primary : plugins,
        querySelectorAll: () => [primary, plugins]
    }];
    page.rerender();
    assert.equal(primary.classList.contains('Mui-selected'), true);
    assert.equal(plugins.classList.contains('Mui-selected'), false);
    page.lifetime.abort();
    assert.equal(primary.classList.contains('Mui-selected'), false);
    assert.equal(plugins.classList.contains('Mui-selected'), true);
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
    assert.ok(page.observers.every(observer => !observer.active));
});

test('cleanup preserves a newer host ARIA value', t => {
    const page = setup(t);
    page.primary.setAttribute('aria-current', 'step');
    page.lifetime.abort();
    assert.equal(page.primary.getAttribute('aria-current'), 'step');
});

test('an already aborted page creates no observers or changes', t => {
    const page = setup(t);
    page.lifetime.abort();
    const count = page.observers.length;
    manualSync.selectManualSyncSidebar(page.lifetime.signal, page.document);
    assert.equal(page.observers.length, count);
    assert.equal(page.primary.classList.contains('Mui-selected'), false);
});
