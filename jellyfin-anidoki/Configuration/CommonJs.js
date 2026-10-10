export const pluginId = 'dceb799c-238e-4a33-aa5e-14fc0b1efe9d';
export const TabGeneral = 0;
export const TabManualSync = 1;
export const providers = [
    { key: 'AniList', name: 'AniList', initials: 'AL' },
    { key: 'Mal', name: 'MyAnimeList', initials: 'MAL' },
    { key: 'Kitsu', name: 'Kitsu', initials: 'KT' },
    { key: 'Annict', name: 'Annict', initials: 'AN' },
    { key: 'Shikimori', name: 'Shikimori', initials: 'SH' },
    { key: 'Simkl', name: 'Simkl', initials: 'SK' }
];
export const providerName = key => providers.find(p => p.key === key)?.name ?? key;
export const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/${name}`);
export const getTabs = () => [
    { href: 'configurationpage?name=AniDoki', name: 'Settings' },
    { href: 'configurationpage?name=AniDoki_ManualSync', name: 'Manual sync' }
];

export function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}
export function icon(name) {
    const namespace = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(namespace, 'svg');
    for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', width: '20', height: '20', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(key, value);
    const paths = name === 'pencil' ? ['M16 3a2.1 2.1 0 0 1 3 3L7 18l-4 1 1-4Z', 'm14 5 3 3'] : name === 'check' ? ['M20 6 9 17l-5-5'] : name === 'eye-off' ? ['M3 3l18 18', 'M10.6 5.1A11 11 0 0 1 12 5c6.5 0 10 7 10 7s-1.1 2.2-3.3 4.1', 'M6.2 6.2C3.4 8.3 2 12 2 12s3.5 7 10 7a11 11 0 0 0 5.8-1.8', 'M10 10a3 3 0 0 0 4 4'] : ['M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z', 'M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0'];
    for (const d of paths) { const path = document.createElementNS(namespace, 'path'); path.setAttribute('d', d); svg.append(path); }
    return svg;
}
export function secretInput(input, name, signal) {
    const group = element('div', 'ad-secret-input');
    const reveal = element('button', 'ad-reveal'); reveal.type = 'button';
    const update = () => {
        const visible = input.type === 'text';
        const label = `${visible ? 'Hide' : 'Show'} ${name} secret`;
        reveal.setAttribute('aria-label', label); reveal.title = label;
        reveal.setAttribute('aria-pressed', String(visible));
        reveal.replaceChildren(icon(visible ? 'eye-off' : 'eye'));
    };
    reveal.addEventListener('click', () => { input.type = input.type === 'password' ? 'text' : 'password'; update(); }, { signal });
    update(); group.append(input, reveal); return group;
}
export function providerIcon(provider) {
    const image = element('img', 'ad-provider-logo');
    image.src = assetUrl(`provider-${provider.key.toLowerCase()}.${provider.key === 'Annict' ? 'png' : 'svg'}`);
    image.alt = ''; image.width = 40; image.height = 40;
    return image;
}
export function refreshFeedback(node, refreshed) {
    node.replaceChildren(); node.hidden = !refreshed;
    if (refreshed) {
        node.title = 'Linked accounts refreshed';
        node.append(icon('check'), element('span', 'ad-sr-only', 'Linked accounts refreshed'));
    }
}
export function status(node, text, tone = '') {
    node.textContent = text; node.dataset.tone = tone;
}
export function setTabs(selected, itemsFn = getTabs, view = document) {
    const root = view.querySelector('#navigationTabs');
    if (!root) return;
    root.replaceChildren();
    itemsFn().forEach((tab, index) => {
        const link = element('a', '', tab.name);
        link.href = `#/${tab.href}`;
        if (index === selected) link.setAttribute('aria-current', 'page');
        root.append(link);
    });
}
export function populateUserList(view, users, selector) {
    const select = view.querySelector(selector); select.replaceChildren();
    users.forEach(user => select.add(new Option(user.Name, user.Id)));
}
export function setProviderSelection(view, list, selector, options = {}) {
    const select = view.querySelector(selector), previous = options.selectedValue || select.value;
    select.replaceChildren();
    list.forEach(provider => select.add(new Option(provider.Name, provider.Key)));
    if ([...select.options].some(option => option.value === previous)) select.value = previous;
}
export const parameterInclude = { ProviderList: 0, LocalIpAddress: 1, LocalPort: 2, Https: 3 };

export async function errorMessage(error) {
    if (typeof error === 'string') return error.slice(0, 1000);
    if (error instanceof Error) return error.message.slice(0, 1000);
    try {
        let message = await error.text();
        if (error.headers?.get('Content-Type')?.includes('json')) {
            try {
                const detail = JSON.parse(message);
                if (typeof detail === 'string') message = detail;
                else if (detail && typeof detail === 'object') message = Object.values(detail.errors ?? {}).flat().join(' ') || detail.detail || detail.title || detail.message || detail.Message || message;
            } catch { /* Keep the original message if the error body is malformed. */ }
        }
        return String(message).slice(0, 1000) || `Request failed (${error.status})`;
    }
    catch { return 'The request could not be confirmed. Check the connection and try again.'; }
}
export async function request(path, { method = 'GET', body, signal, accept } = {}) {
    const headers = {};
    // ApiClient.ajax 1.11 ignores AbortSignal. Reuse its public auth-header
    // method with fetch so view loads can actually stop and mutations never retry.
    ApiClient.setRequestHeaders(headers);
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (accept) headers.Accept = accept;
    const response = await fetch(ApiClient.getUrl(path), { method, headers,
        body: body === undefined ? undefined : JSON.stringify(body), signal, credentials: 'same-origin' });
    if (!response.ok) throw response;
    return response;
}
export async function json(path, options) {
    const response = await request(path, { ...options, accept: 'application/json' });
    if (response.status === 204) return undefined;
    return response.headers.get('Content-Type')?.includes('json') ? response.json() : response.text();
}
export function safeExternalUrl(address) {
    const url = new URL(address);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error('Use an HTTP or HTTPS address without credentials.');
    return url.href;
}
export function authorizationLink(container, url, name) {
    container.replaceChildren();
    const link = element('a', 'ad-button', `Continue to ${name}`);
    link.href = safeExternalUrl(url); link.target = '_blank'; link.rel = 'noopener noreferrer';
    container.append(link);
}
export function ensureStyles() {
    if (document.querySelector('link[data-anidoki-styles]')) return;
    const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = assetUrl('styles.css');
    link.dataset.anidokiStyles = ''; document.head.append(link);
}
export function prepareStickyShell(root, signal) {
    // Host content wrappers clip overflow without scrolling, which traps sticky
    // controls. Mark only this active plugin's content ancestors and restore on hide.
    const marked = [];
    for (let node = root.parentElement; node && node !== document.body; node = node.parentElement) {
        if (node.matches('[data-role="content"]') && !node.classList.contains('ad-content-shell')) {
            node.classList.add('ad-content-shell'); marked.push(node);
        }
    }
    signal.addEventListener('abort', () => marked.forEach(node => node.classList.remove('ad-content-shell')), { once: true });
}
// One listener set per active view. State is owned by the page factory and survives
// viewhide only while Jellyfin retains that view in its cache.
export function mountPage(view, start) {
    let active;
    const show = () => {
        active?.abort(); active = new AbortController();
        const signal = active.signal;
        Promise.resolve(start(signal)).catch(async error => {
            const message = await errorMessage(error);
            if (!signal.aborted) status(view.querySelector('[data-page-status]'), message, 'error');
        });
    };
    view.addEventListener('viewshow', show);
    view.addEventListener('viewhide', () => active?.abort());
    return show;
}
export function observeSaveBar(root, signal) {
    const bar = root.querySelector('.ad-save-bar'); if (!bar) return;
    const observer = new ResizeObserver(() => root.style.setProperty('--ad-save-height', `${bar.offsetHeight + 48}px`));
    observer.observe(bar); signal.addEventListener('abort', () => observer.disconnect(), { once: true });
}
export function bindNavigation(root, signal) {
    const links = [...root.querySelectorAll('.ad-section-nav a')];
    const revealPill = link => {
        const nav = link.parentElement;
        const edge = nav.getBoundingClientRect(), item = link.getBoundingClientRect();
        if (item.left < edge.left) nav.scrollLeft += item.left - edge.left;
        else if (item.right > edge.right) nav.scrollLeft += item.right - edge.right;
    };
    const activate = link => {
        links.forEach(item => item.removeAttribute('aria-current'));
        link.setAttribute('aria-current', 'location');
        revealPill(link);
    };
    links.forEach(link => {
        link.addEventListener('click', event => {
            event.preventDefault(); const section = root.querySelector(link.dataset.section);
            section?.scrollIntoView({ block: 'start', behavior: 'instant' }); activate(link);
            revealPill(link);
        }, { signal });
        link.addEventListener('focus', () => revealPill(link), { signal });
    });
    if (links.length) activate(links[0]);
    const observer = new IntersectionObserver(entries => {
        const entry = entries.filter(item => item.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
        if (entry) { const link = links.find(item => item.dataset.section === `#${entry.target.id}`); if (link) activate(link); }
    }, { rootMargin: '-90px 0px -55% 0px' });
    links.forEach(link => { const section = root.querySelector(link.dataset.section); if (section) observer.observe(section); });
    signal.addEventListener('abort', () => observer.disconnect(), { once: true });
}
export function bindDraftWarning(view, isDirty, signal) {
    // A viewbeforehide cancellation is not supported by the host. Guard explicit
    // navigation links during this view's active lifetime without patching its router.
    document.addEventListener('click', event => {
        const link = event.target.closest?.('a[href]');
        if (!link || link.closest('.ad-section-nav') || link.target === '_blank' || !isDirty()) return;
        const url = new URL(link.href, location.href);
        if (url.href === location.href || url.origin !== location.origin) return;
        if (!window.confirm('Leave this page? Unsaved changes may be lost if Jellyfin reloads it.')) {
            event.preventDefault(); event.stopImmediatePropagation();
        }
    }, { capture: true, signal });
    const warning = event => { if (isDirty()) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', warning, { signal });
}

let libraryGroupCounter = 0;

export function renderLibraries(container, preferences, libraries, signal, onChange, choice = {}) {
    container.replaceChildren();
    container.classList.add('ad-library-controls');
    choice.mode ??= preferences.LibraryToCheck.length ? 'selected' : 'all';
    choice.selected ??= [...preferences.LibraryToCheck];
    const group = `anidoki-libraries-${++libraryGroupCounter}`;
    const modes = element('div', 'ad-library-modes');
    const list = element('div', 'ad-library-list');
    const message = element('p', 'ad-help ad-library-help');
    const update = () => {
        list.hidden = choice.mode === 'all'; preferences.LibraryToCheck = choice.mode === 'all' ? [] : [...choice.selected];
        message.textContent = choice.mode === 'all' ? 'All current and future libraries are included.' : !choice.selected.length ? 'Pick at least one library, or choose All libraries.' : 'Unavailable selections stay saved until you remove them.';
        onChange();
    };
    for (const [value, title, help] of [['all', 'All libraries', 'Track matching anime across all current and future libraries.'], ['selected', 'Only the ones I pick', 'Track matching anime only in the libraries you select below.']]) {
        const label = element('label', 'ad-scope-card'); const input = element('input');
        input.type = 'radio'; input.name = group; input.value = value; input.checked = choice.mode === value;
        input.addEventListener('change', () => { choice.mode = value; update(); }, { signal });
        const text = element('span'); text.append(element('strong', '', title), element('span', 'ad-help', help));
        label.append(input, text); modes.append(label);
    }
    const choices = libraries.map(library => ({ id: library.Id ?? library.ItemId, name: library.Name }));
    for (const id of choice.selected) if (!choices.some(item => item.id === id)) choices.push({ id, name: `Unavailable library (${id})` });
    for (const item of choices) {
        const label = element('label', 'ad-library-choice'); const input = element('input', 'ad-choice-checkbox'); input.type = 'checkbox'; input.checked = choice.selected.includes(item.id);
        input.addEventListener('change', () => { choice.selected = input.checked ? [...new Set([...choice.selected, item.id])] : choice.selected.filter(id => id !== item.id); update(); }, { signal });
        label.append(input, element('span', '', item.name)); list.append(label);
    }
    if (!choices.length) list.append(element('p', 'ad-help', 'No libraries are available.'));
    container.append(modes, list, message); update();
    return () => choice.mode === 'all' || choice.selected.length > 0;
}

export function renderAccounts(container, { linked, visible, admin = false, userName, action }, signal) {
    container.replaceChildren();
    for (const provider of providers.filter(item => visible.includes(item.key) || linked.includes(item.key))) {
        const connected = linked.includes(provider.key);
        const row = element('div', 'ad-account-row');
        const title = element('div', 'ad-row-name'); title.append(element('strong', '', provider.name));
        title.append(element('div', 'ad-help', connected ? 'Linked' : provider.key === 'Kitsu' ? 'Direct sign-in' : provider.key === 'Annict' ? 'Personal token' : 'Not linked'));
        row.append(providerIcon(provider), title);
        const feedback = element('p', 'ad-status'); feedback.setAttribute('role', 'status');
        const detail = element('div', 'ad-account-detail'); detail.hidden = true;
        const buttons = [];
        const run = async (kind, credentials = {}) => {
            buttons.forEach(button => button.disabled = true); status(feedback, 'Working…');
            try {
                const result = await action(kind, provider.key, credentials);
                if (signal.aborted) return;
                if (result?.authorizationUrl) { authorizationLink(detail, result.authorizationUrl, provider.name); detail.hidden = false; }
                else status(feedback, result?.message ?? (kind === 'test' ? 'Connection verified.' : 'Account updated.'), 'success');
            } catch (error) {
                const message = await errorMessage(error);
                if (!signal.aborted) status(feedback, message, 'error');
            }
            finally { if (!signal.aborted) buttons.forEach(button => button.disabled = false); }
        };
        const button = (label, handler) => {
            const node = element('button', 'ad-button', label); node.type = 'button'; node.addEventListener('click', handler, { signal }); buttons.push(node); return node;
        };
        if (connected) {
            row.append(button('Test', () => run('test')));
            row.append(button('Disconnect', () => {
                detail.replaceChildren(element('p', 'ad-help', `Disconnect ${provider.name} for ${userName}? This takes effect immediately.`));
                detail.append(button('Confirm disconnect', () => run('disconnect')), button('Keep linked', () => detail.hidden = true)); detail.hidden = false;
            }));
        } else if (provider.key === 'Annict' && !admin) {
            row.append(element('span', 'ad-help', 'Ask your administrator to link a personal token.'));
        } else if (provider.key === 'Kitsu' || provider.key === 'Annict') {
            row.append(button('Link', () => {
                detail.replaceChildren();
                const credentials = {};
                const fields = element('div', 'ad-account-fields');
                for (const [key, label, type] of provider.key === 'Kitsu' ? [['Username', 'Kitsu username or email', 'text'], ['Password', 'Kitsu password', 'password']] : [['Token', `Annict token for ${userName}`, 'password']]) {
                    const field = element('label', 'ad-field', label); const input = element('input', 'ad-input'); input.type = type; input.autocomplete = 'off'; field.append(input); fields.append(field); credentials[key] = input;
                }
                detail.append(fields);
                if (provider.key === 'Annict') detail.append(element('p', 'ad-help', 'The token is stored in plain text in the server configuration.'));
                const actions = element('div', 'ad-actions');
                const link = button('Link account', async () => {
                    const values = Object.fromEntries(Object.entries(credentials).map(([key, input]) => [key, input.value]));
                    if (Object.values(values).some(value => !value)) { status(feedback, 'Fill in all account fields.', 'error'); return; }
                    try { await run('link', values); } finally { for (const input of Object.values(credentials)) if (input.type === 'password') input.value = ''; }
                });
                link.classList.add('ad-button-primary');
                actions.append(link, button('Close', () => { detail.replaceChildren(); detail.hidden = true; }));
                detail.append(actions); detail.hidden = false;
                signal.addEventListener('abort', () => { detail.replaceChildren(); }, { once: true });
            }));
        } else {
            row.append(button('Link', () => run('link')));
        }
        row.append(detail, feedback); container.append(row);
    }
}
