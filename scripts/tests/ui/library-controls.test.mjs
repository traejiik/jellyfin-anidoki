import test from 'node:test';
import assert from 'node:assert/strict';
import { renderLibraries } from '../../../jellyfin-anidoki/Configuration/CommonJs.js';

class DomElement extends EventTarget {
    children = [];
    classList = { add() {} };

    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
}

function useHttpContext(t) {
    const descriptors = ['document', 'crypto'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]);
    Object.defineProperty(globalThis, 'document', { configurable: true, value: { createElement: () => new DomElement() } });
    Object.defineProperty(globalThis, 'crypto', { configurable: true, value: {} });
    t.after(() => {
        for (const [key, descriptor] of descriptors) {
            if (descriptor) Object.defineProperty(globalThis, key, descriptor);
            else delete globalThis[key];
        }
    });
}

const modeInputs = container => container.children[0].children.map(label => label.children[0]);

test('library radio groups remain independent without secure-context randomUUID', t => {
    useHttpContext(t);
    const first = new DomElement();
    const second = new DomElement();
    renderLibraries(first, { LibraryToCheck: [] }, [], new AbortController().signal, () => {});
    renderLibraries(second, { LibraryToCheck: [] }, [], new AbortController().signal, () => {});

    const [firstAll, firstSelected] = modeInputs(first);
    const [secondAll, secondSelected] = modeInputs(second);
    assert.ok(firstAll.name);
    assert.equal(firstAll.name, firstSelected.name);
    assert.equal(secondAll.name, secondSelected.name);
    assert.notEqual(firstAll.name, secondAll.name);
});

test('HTTP library controls serialize All and validate Selected choices', t => {
    useHttpContext(t);
    const container = new DomElement();
    const preferences = { LibraryToCheck: [] };
    const choice = {};
    const valid = renderLibraries(container, preferences, [{ Id: 'anime', Name: 'Anime' }], new AbortController().signal, () => {}, choice);
    const [all, selected] = modeInputs(container);
    assert.equal(all.checked, true);
    assert.equal(valid(), true);
    assert.deepEqual(preferences.LibraryToCheck, []);

    selected.dispatchEvent(new Event('change'));
    assert.equal(valid(), false);
    const checkbox = container.children[1].children[0].children[0];
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    assert.equal(valid(), true);
    assert.deepEqual(preferences.LibraryToCheck, ['anime']);

    all.dispatchEvent(new Event('change'));
    assert.equal(valid(), true);
    assert.deepEqual(preferences.LibraryToCheck, []);
    selected.dispatchEvent(new Event('change'));
    assert.deepEqual(preferences.LibraryToCheck, ['anime']);
});
