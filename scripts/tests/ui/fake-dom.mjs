// A small DOM for lifecycle tests: elements, classes, attributes, focus and the selectors the popup and Jellyfin's page use
// (tag, .class, #id, [attr], [attr=value], :not(.class), descendant and comma lists). No layout, no text nodes.

function parseCompound(text) {
    const parts = { tag: null, id: null, classes: [], attrs: [], not: [] };
    const pattern = /(:not\(([^)]+)\))|(\.[\w-]+)|(#[\w-]+)|(\[([\w-]+)(?:=["']?([^"'\]]*)["']?)?\])|([\w-]+|\*)/g;
    for (const match of text.matchAll(pattern)) {
        if (match[1])
            parts.not.push(parseCompound(match[2]));
        else if (match[3])
            parts.classes.push(match[3].slice(1));
        else if (match[4])
            parts.id = match[4].slice(1);
        else if (match[5])
            parts.attrs.push([match[6], match[7]]);
        else if (match[8] && match[8] !== '*')
            parts.tag = match[8].toUpperCase();
    }
    return parts;
}

function matchesCompound(element, parts) {
    if (parts.tag && element.tagName !== parts.tag)
        return false;
    if (parts.id && element.id !== parts.id)
        return false;
    if (parts.classes.some(name => !element.classList.contains(name)))
        return false;
    for (const [name, value] of parts.attrs) {
        const actual = element.getAttribute(name);
        if (actual === null || (value !== undefined && actual !== value))
            return false;
    }
    return !parts.not.some(inner => matchesCompound(element, inner));
}

function matches(element, selector) {
    return (selector.match(/(?:\[[^\]]*\]|\([^)]*\)|[^,[(])+/g) ?? []).some(one => {
        const chain = (one.trim().match(/(?:\[[^\]]*\]|\([^)]*\)|[^\s[(])+/g) ?? []).map(parseCompound);
        if (!matchesCompound(element, chain.at(-1)))
            return false;
        let index = chain.length - 2, ancestor = element.parent;
        while (index >= 0 && ancestor) {
            if (ancestor.tagName && matchesCompound(ancestor, chain[index]))
                index--;
            ancestor = ancestor.parent;
        }
        return index < 0;
    });
}

export class Element extends EventTarget {
    constructor(tag, doc) {
        super();
        this.tagName = tag.toUpperCase();
        this.ownerDocument = doc;
        this.children = [];
        this.attributes = {};
        // As in a browser, dataset is a view of the data-* attributes.
        const attribute = key => `data-${String(key).replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`;
        this.dataset = new Proxy({}, {
            get: (_, key) => typeof key === 'string' ? this.attributes[attribute(key)] : undefined,
            set: (_, key, value) => { this.attributes[attribute(key)] = String(value); return true; },
            deleteProperty: (_, key) => { delete this.attributes[attribute(key)]; return true; },
            has: (_, key) => attribute(key) in this.attributes
        });
        this.style = { setProperty: (name, value) => { this.style[name] = value; }, removeProperty: name => { delete this.style[name]; } };
        this.className = '';
        this.textContent = '';
        this.hidden = false;
        this.parent = null;
        this.value = '';
    }
    get classList() {
        const self = this, names = () => self.className.split(/\s+/).filter(Boolean);
        return {
            contains: name => names().includes(name),
            add: (...added) => { self.className = [...new Set([...names(), ...added])].join(' '); },
            remove: (...removed) => { self.className = names().filter(name => !removed.includes(name)).join(' '); },
            toggle: (name, force) => { const on = force ?? !names().includes(name); on ? this.classList.add(name) : this.classList.remove(name); return on; }
        };
    }
    get parentElement() { return this.parent?.tagName ? this.parent : null; }
    get isConnected() { let node = this; while (node.parent) node = node.parent; return node === this.ownerDocument; }
    get id() { return this.attributes.id ?? ''; }
    set id(value) { this.attributes.id = String(value); }
    get firstElementChild() { return this.children[0] ?? null; }
    append(...nodes) { for (const node of nodes) { if (!node) continue; node.remove(); node.parent = this; this.children.push(node); } }
    prepend(...nodes) { for (const node of [...nodes].reverse()) { node.remove(); node.parent = this; this.children.unshift(node); } }
    insertBefore(node, reference) {
        node.remove(); node.parent = this;
        const index = reference ? this.children.indexOf(reference) : -1;
        index < 0 ? this.children.push(node) : this.children.splice(index, 0, node);
        return node;
    }
    replaceChildren(...nodes) { for (const child of [...this.children]) child.remove(); this.append(...nodes); }
    remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); this.parent = null; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    hasAttribute(name) { return name in this.attributes; }
    removeAttribute(name) { delete this.attributes[name]; }
    toggleAttribute(name, force) { const on = force ?? !(name in this.attributes); on ? this.setAttribute(name, '') : this.removeAttribute(name); return on; }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
    querySelectorAll(selector) { return this.descendants().filter(element => matches(element, selector)); }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
    matches(selector) { return matches(this, selector); }
    closest(selector) { let node = this; while (node?.tagName) { if (matches(node, selector)) return node; node = node.parent; } return null; }
    focus() { this.ownerDocument.activeElement = this; }
    blur() { if (this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = this.ownerDocument.body; }
    click() { this.dispatchEvent(new Event('click')); }
    getBoundingClientRect() { return { width: 0, height: 0, top: 0, left: 0 }; }
    scrollIntoView() {}
}

export function createDocument() {
    const doc = new EventTarget();
    doc.createElement = tag => new Element(tag, doc);
    doc.documentElement = doc.createElement('html');
    doc.documentElement.parent = doc;
    doc.head = doc.createElement('head');
    doc.body = doc.createElement('body');
    doc.documentElement.append(doc.head, doc.body);
    doc.activeElement = doc.body;
    doc.hidden = false;
    doc.querySelector = selector => doc.documentElement.querySelector(selector);
    doc.querySelectorAll = selector => doc.documentElement.querySelectorAll(selector);
    doc.getElementById = id => doc.documentElement.querySelector(`#${id}`);
    return doc;
}

/** A window with fake timers, history and location. advance(ms) runs due timers in order. */
export function createWindow(href) {
    const win = new EventTarget();
    let now = 0, serial = 0;
    const timers = new Map();
    win.timers = timers;
    win.setTimeout = (fn, delay = 0) => { const id = ++serial; timers.set(id, { fn, at: now + delay }); return id; };
    win.clearTimeout = id => timers.delete(id);
    win.requestAnimationFrame = fn => win.setTimeout(fn, 16);
    win.cancelAnimationFrame = id => timers.delete(id);
    win.advance = async ms => {
        const until = now + ms;
        for (;;) {
            await flush();
            const next = [...timers].filter(([, timer]) => timer.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
            if (!next) break;
            timers.delete(next[0]); now = next[1].at; await next[1].fn();
            await flush();
        }
        now = until;
    };
    const entries = [href];
    let index = 0;
    win.location = { get href() { return entries[index]; }, get search() { return new URL(entries[index]).search; }, get hash() { return new URL(entries[index]).hash; }, get pathname() { return new URL(entries[index]).pathname; } };
    win.history = {
        get length() { return entries.length; },
        pushState(state, title, url) { entries.splice(index + 1); entries.push(new URL(url, entries[index]).href); index++; },
        replaceState(state, title, url) { entries[index] = new URL(url, entries[index]).href; },
        back() { if (index > 0) { index--; win.dispatchEvent(new Event('popstate')); } }
    };
    win.setHash = hash => { entries[index] = new URL(hash, entries[index]).href; win.dispatchEvent(new Event('hashchange')); };
    return win;
}

export const flush = async () => { for (let i = 0; i < 12; i++) await new Promise(resolve => setImmediate(resolve)); };

export function keydown(target, key, extra = {}) {
    const event = new Event('keydown', { bubbles: false, cancelable: true });
    Object.assign(event, { key, shiftKey: false, ...extra });
    target.dispatchEvent(event);
    return event;
}
