import test from 'node:test';
import assert from 'node:assert/strict';
import { bindEditor, bindPageActions, describeMapping } from '../../../jellyfin-anidoki/Configuration/MatchEditorJs.js';

// Just enough of an element to count listeners and route events by target, as the page's delegation needs.
class Fake {
    constructor(name = 'root', parent = null) { this.name = name; this.parent = parent; this.listeners = []; this.dataset = {}; this.tagName = 'DIV'; }
    addEventListener(type, handler, { signal } = {}) {
        if (signal?.aborted) return;
        const entry = { type, handler };
        this.listeners.push(entry);
        signal?.addEventListener('abort', () => { this.listeners = this.listeners.filter(item => item !== entry); }, { once: true });
    }
    count(type) { return this.listeners.filter(entry => entry.type === type).length; }
    dispatch(type, event) { for (const entry of this.listeners.filter(item => item.type === type)) entry.handler({ preventDefault() {}, ...event }); }
    contains(node) { for (let item = node; item; item = item.parent) if (item === this) return true; return false; }
    closest(selector) {
        for (let item = this; item; item = item.parent) {
            if (selector === '[data-action]' && item.dataset.action) return item;
            if (selector.includes('#matchEditor') && ['matchEditor', 'entryPicker'].includes(item.name)) return item;
        }
        return null;
    }
    querySelector() { return null; }
}

const button = (parent, action, row) => Object.assign(new Fake('button', parent), { dataset: { action, ...(row ? { row } : {}) } });

function editorCalls() {
    const calls = [];
    return { calls, handlers: {
        change: (...args) => calls.push(['change', ...args]), edit: (...args) => calls.push(['edit', ...args]),
        search: query => calls.push(['search', query]), pick: index => calls.push(['pick', index]), close: () => calls.push(['close'])
    } };
}

test('showing the page again replaces every editor listener instead of adding to them', () => {
    const container = new Fake('matchEditor'), picker = new Fake('entryPicker');
    const { calls, handlers } = editorCalls();
    let lifetime;
    for (let shown = 0; shown < 3; shown++) {
        lifetime?.abort();
        lifetime = new AbortController();
        bindEditor(container, picker, handlers, lifetime.signal);
    }
    for (const type of ['input', 'change', 'click']) assert.equal(container.count(type), 1, type);
    for (const type of ['submit', 'click', 'keydown']) assert.equal(picker.count(type), 1, type);
    container.dispatch('click', { target: button(container, 'add-range') });
    assert.deepEqual(calls, [['edit', 'add-range', null]], 'one click, one call');
    lifetime.abort();
    assert.equal(container.listeners.length + picker.listeners.length, 0, 'leaving the page removes them all');
});

test('typing reports the field without redrawing, and a kind change is structural', () => {
    const container = new Fake('matchEditor'), picker = new Fake('entryPicker');
    const { calls, handlers } = editorCalls();
    bindEditor(container, picker, handlers, new AbortController().signal);
    container.dispatch('input', { target: { tagName: 'INPUT', value: '12', dataset: { row: 'row-1', field: 'sourceEnd' } } });
    container.dispatch('input', { target: { tagName: 'TEXTAREA', value: 'why', dataset: { field: 'note' } } });
    container.dispatch('input', { target: { tagName: 'SELECT', value: 'target', dataset: { row: 'row-2', field: 'kind' } } });
    container.dispatch('change', { target: { tagName: 'SELECT', value: 'target', dataset: { row: 'row-2', field: 'kind' } } });
    assert.deepEqual(calls, [['change', 'row-1', 'sourceEnd', '12'], ['change', null, 'note', 'why'], ['edit', 'kind', 'row-2', 'target']]);
});

test('the picker searches, picks and closes, and Escape closes it', () => {
    const container = new Fake('matchEditor'), picker = new Fake('entryPicker');
    picker.querySelector = () => ({ value: 'Steel Ball Run' });
    const { calls, handlers } = editorCalls();
    bindEditor(container, picker, handlers, new AbortController().signal);
    picker.dispatch('submit', {});
    picker.dispatch('click', { target: Object.assign(button(picker, 'pick-entry'), { dataset: { action: 'pick-entry', index: '2' } }) });
    picker.dispatch('keydown', { key: 'Escape' });
    picker.dispatch('keydown', { key: 'Enter' });
    assert.deepEqual(calls, [['search', 'Steel Ball Run'], ['pick', 2], ['close']]);
});

test('page actions are bound once per lifetime and leave the editor’s own buttons alone', () => {
    const root = new Fake('root');
    const editor = new Fake('matchEditor', root);
    const runs = [];
    let lifetime;
    for (let shown = 0; shown < 2; shown++) {
        lifetime?.abort();
        lifetime = new AbortController();
        bindPageActions(root, { save: () => runs.push('save'), 'remove-row': () => runs.push('wrong') }, lifetime.signal);
    }
    assert.equal(root.count('click'), 1);
    root.dispatch('click', { target: button(root, 'save') });
    root.dispatch('click', { target: button(editor, 'remove-row', 'row-1') });
    root.dispatch('click', { target: button(root, 'unknown') });
    assert.deepEqual(runs, ['save']);
});

test('a saved mapping reads as lines, and a deleted one says so', () => {
    const target = { id: 1, title: 'Part 1' };
    assert.deepEqual(describeMapping({ ranges: [{ sourceStart: 1, sourceEnd: 12, target, targetStart: 1 }, { sourceStart: 13, target, targetStart: 13 }],
        exceptions: [{ sourceEpisode: 5, kind: 'ignore' }, { sourceEpisode: 0, kind: 'target', target, targetEpisode: 1 }] }), [
        'Episodes 1–12 → Part 1, from episode 1', 'Episodes 13–on → Part 1, from episode 13', 'Episode 5 ignored', 'Episode 0 → Part 1, episode 1'
    ]);
    assert.deepEqual(describeMapping({ ranges: [], exceptions: [] }), ['Nothing (deleted)']);
});
