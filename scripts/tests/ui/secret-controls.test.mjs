import test from 'node:test';
import assert from 'node:assert/strict';
import * as common from '../../../jellyfin-anidoki/Configuration/CommonJs.js';

class Node extends EventTarget {
    children = [];
    attributes = new Map();
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
}

function documentFor(t) {
    const prior = Object.getOwnPropertyDescriptor(globalThis, 'document');
    Object.defineProperty(globalThis, 'document', { configurable: true, value: {
        createElement: () => new Node(), createElementNS: () => new Node()
    } });
    t.after(() => prior ? Object.defineProperty(globalThis, 'document', prior) : delete globalThis.document);
}

test('revealing and hiding an editable secret preserves the pasted value', t => {
    documentFor(t);
    assert.equal(typeof common.secretInput, 'function');
    const input = new Node();
    input.type = 'password'; input.value = 'pasted & literal + secret';
    const control = common.secretInput(input, 'AniList', new AbortController().signal);
    const reveal = control.children[1];
    assert.equal(reveal.getAttribute('aria-label'), 'Show AniList secret');
    reveal.dispatchEvent(new Event('click'));
    assert.equal(input.type, 'text');
    assert.equal(input.value, 'pasted & literal + secret');
    assert.equal(reveal.getAttribute('aria-pressed'), 'true');
    assert.equal(reveal.getAttribute('aria-label'), 'Hide AniList secret');
    input.value = 'replacement pasted while visible';
    reveal.dispatchEvent(new Event('click'));
    assert.equal(input.type, 'password');
    assert.equal(input.value, 'replacement pasted while visible');
    assert.notEqual(input.readOnly, true);
    assert.equal(reveal.getAttribute('aria-pressed'), 'false');
});

test('a hidden view no longer responds to secret reveal clicks', t => {
    documentFor(t);
    assert.equal(typeof common.secretInput, 'function');
    const lifetime = new AbortController();
    const input = new Node(); input.type = 'password'; input.value = 'unchanged';
    const control = common.secretInput(input, 'Kitsu', lifetime.signal);
    lifetime.abort();
    control.children[1].dispatchEvent(new Event('click'));
    assert.equal(input.type, 'password');
    assert.equal(input.value, 'unchanged');
});
