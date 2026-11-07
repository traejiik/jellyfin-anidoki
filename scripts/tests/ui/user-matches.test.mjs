import test from 'node:test';
import assert from 'node:assert/strict';
import { guardSettingsForm } from '../../../jellyfin-anidoki/Configuration/UserMatchesJs.js';

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
