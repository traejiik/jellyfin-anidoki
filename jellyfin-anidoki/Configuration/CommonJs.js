export const pluginId = 'dceb799c-238e-4a33-aa5e-14fc0b1efe9d';
export const TabGeneral = 0;
export const TabMatches = 1;
export const TabManualSync = 2;
export const providers = [
    { key: 'AniList', name: 'AniList', initials: 'AL' },
    { key: 'Mal', name: 'MyAnimeList', initials: 'MAL' },
    { key: 'Kitsu', name: 'Kitsu', initials: 'KT' },
    { key: 'Annict', name: 'Annict', initials: 'AN' },
    { key: 'Shikimori', name: 'Shikimori', initials: 'SH' },
    { key: 'Simkl', name: 'Simkl', initials: 'SK' }
];
export const providerName = key => providers.find(p => p.key === key)?.name ?? key;
// Asset URLs carry a per-load path segment so a caching proxy cannot serve another release's files;
// relative module imports resolve inside the same segment. Entry points build the same URL before this loads.
export const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/v/${globalThis.anidokiAssetVersion ??= Date.now().toString(36)}/${name}`);
export const getTabs = () => [
    { href: 'configurationpage?name=AniDoki', name: 'Settings' },
    { href: 'configurationpage?name=AniDoki_Matches', name: 'Matches' },
    { href: 'configurationpage?name=AniDoki_ManualSync', name: 'Manual sync' }
];

export function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}
// Top-bar summary on the admin pages: how many Jellyfin users have at least one linked tracker.
export function linkedUsersBadge(users, userConfig = []) {
    const count = users.filter(user => userConfig.find(item => item.UserId === user.Id)?.UserApiAuth?.length).length;
    return element('span', 'doki-badge', `${count} ${count === 1 ? 'user' : 'users'} linked`);
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
    const group = element('div', 'doki-secret-input');
    const reveal = element('button', 'doki-reveal'); reveal.type = 'button';
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
    const image = element('img', 'doki-provider-logo');
    image.src = assetUrl(`provider-${provider.key.toLowerCase()}.${provider.key === 'Annict' ? 'png' : 'svg'}`);
    image.alt = ''; image.width = 40; image.height = 40;
    return image;
}
export function refreshFeedback(node, refreshed) {
    node.replaceChildren(); node.hidden = !refreshed;
    if (refreshed) {
        node.title = 'Linked accounts refreshed';
        node.append(icon('check'), element('span', 'doki-sr-only', 'Linked accounts refreshed'));
    }
}
export function status(node, text, tone = '') {
    node.textContent = text; node.dataset.tone = tone;
}
// navigator.clipboard only exists on HTTPS or localhost; Jellyfin is often reached over http on a LAN address.
export async function copyInput(input) {
    if (navigator.clipboard) {
        try { await navigator.clipboard.writeText(input.value); return true; } catch { /* Blocked: try the selection. */ }
    }
    input.focus(); input.select();
    try { return document.execCommand('copy'); } catch { return false; }
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
    const link = element('a', 'doki-button', `Continue to ${name}`);
    // Same tab: the callback page links back here, and the settings page reloads the linked accounts.
    link.href = safeExternalUrl(url); link.rel = 'noreferrer';
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
        if (node.matches('[data-role="content"]') && !node.classList.contains('doki-content-shell')) {
            node.classList.add('doki-content-shell'); marked.push(node);
        }
    }
    signal.addEventListener('abort', () => marked.forEach(node => node.classList.remove('doki-content-shell')), { once: true });
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
    const bar = root.querySelector('.doki-save-bar'); if (!bar) return;
    const observer = new ResizeObserver(() => root.style.setProperty('--doki-save-height', `${bar.offsetHeight + 48}px`));
    observer.observe(bar); signal.addEventListener('abort', () => observer.disconnect(), { once: true });
}
export function bindNavigation(root, signal) {
    const links = [...root.querySelectorAll('.doki-section-nav a')];
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
        if (!link || link.closest('.doki-section-nav') || link.target === '_blank' || !isDirty()) return;
        const url = new URL(link.href, location.href);
        if (url.href === location.href || url.origin !== location.origin) return;
        if (!window.confirm('Leave this page? Unsaved changes may be lost if Jellyfin reloads it.')) {
            event.preventDefault(); event.stopImmediatePropagation();
        }
    }, { capture: true, signal });
    const warning = event => { if (isDirty()) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', warning, { signal });
}

export function renderAccounts(container, { linked, visible, admin = false, userName, action }, signal) {
    container.replaceChildren();
    for (const provider of providers.filter(item => visible.includes(item.key) || linked.includes(item.key))) {
        const connected = linked.includes(provider.key);
        const row = element('div', 'doki-account-row');
        const title = element('div', 'doki-row-name'); title.append(element('strong', '', provider.name));
        title.append(element('div', 'doki-help', connected ? 'Linked' : provider.key === 'Kitsu' ? 'Direct sign-in' : provider.key === 'Annict' ? 'Personal token' : 'Not linked'));
        row.append(providerIcon(provider), title);
        const feedback = element('p', 'doki-status'); feedback.setAttribute('role', 'status');
        const detail = element('div', 'doki-account-detail'); detail.hidden = true;
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
            const node = element('button', 'doki-button', label); node.type = 'button'; node.addEventListener('click', handler, { signal }); buttons.push(node); return node;
        };
        if (connected) {
            row.append(button('Test', () => run('test')));
            row.append(button('Disconnect', () => {
                detail.replaceChildren(element('p', 'doki-help', `Disconnect ${provider.name} for ${userName}? This takes effect immediately.`));
                detail.append(button('Confirm disconnect', () => run('disconnect')), button('Keep linked', () => detail.hidden = true)); detail.hidden = false;
            }));
        } else if (provider.key === 'Annict' && !admin) {
            row.append(element('span', 'doki-help', 'Ask your administrator to link a personal token.'));
        } else if (provider.key === 'Kitsu' || provider.key === 'Annict') {
            row.append(button('Link', () => {
                detail.replaceChildren();
                const credentials = {};
                const fields = element('div', 'doki-account-fields');
                for (const [key, label, type] of provider.key === 'Kitsu' ? [['Username', 'Kitsu email', 'email'], ['Password', 'Kitsu password', 'password']] : [['Token', `Annict token for ${userName}`, 'password']]) {
                    const field = element('label', 'doki-field', label); const input = element('input', 'doki-input'); input.type = type; input.autocomplete = 'off'; field.append(input); fields.append(field); credentials[key] = input;
                }
                detail.append(fields);
                if (provider.key === 'Annict') detail.append(element('p', 'doki-help', 'The token is kept in the AniDōki database on the server, not in the plugin configuration.'));
                const actions = element('div', 'doki-actions');
                const link = button('Link account', async () => {
                    const values = Object.fromEntries(Object.entries(credentials).map(([key, input]) => [key, input.value]));
                    if (Object.values(values).some(value => !value)) { status(feedback, 'Fill in all account fields.', 'error'); return; }
                    try { await run('link', values); } finally { for (const input of Object.values(credentials)) if (input.type === 'password') input.value = ''; }
                });
                link.classList.add('doki-button-primary');
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

// Jellyfin 12.2 matches main-menu plugin links by their exact page name. Hidden
// sibling pages (Manual sync, Matches) otherwise select Plugins. Keep this
// compatibility correction confined to the visible hidden page and the existing dashboard links.
export function selectPluginSidebar(page, signal, doc = document) {
    if (signal.aborted || !doc.body || typeof MutationObserver === 'undefined') return;
    const hiddenPage = page, primaryPage = 'AniDoki';
    const snapshots = new Map();
    const route = href => {
        const hash = href?.slice(href.indexOf('#') + 1) ?? '';
        const [path, query] = hash.split('?');
        return { path, params: new URLSearchParams(query) };
    };
    const currentRoute = () => route(doc.defaultView?.location.hash);
    const onHiddenPage = () => {
        const current = currentRoute();
        return current.path === '/configurationpage' && current.params.get('name') === hiddenPage;
    };
    const selectionObserver = new MutationObserver(refresh);
    const mountObserver = new MutationObserver(refresh);
    function correct(link, selected, section) {
        if (!link) return;
        if (!snapshots.has(link)) snapshots.set(link, {
            selected: link.classList.contains('Mui-selected'), current: link.getAttribute('aria-current'), section
        });
        link.classList.toggle('Mui-selected', selected);
        if (selected) link.setAttribute('aria-current', 'page');
        else link.removeAttribute('aria-current');
    }
    function refresh() {
        if (signal.aborted || !onHiddenPage()) return;
        // Disconnect while writing so our own class changes do not feed an
        // observer loop. Only the plugin section receives attribute observation.
        selectionObserver.disconnect();
        for (const section of doc.querySelectorAll('ul[aria-labelledby="plugins-subheader"]')) {
            const primary = section.querySelector('a[href="#/configurationpage?name=AniDoki"]');
            const plugins = section.querySelector('a[href="#/dashboard/plugins"]');
            if (!primary || !plugins) continue;
            correct(primary, true, section);
            correct(plugins, false, section);
            selectionObserver.observe(section, { attributes: true, subtree: true, attributeFilter: ['class', 'href', 'aria-current'] });
        }
    }
    signal.addEventListener('abort', () => {
        selectionObserver.disconnect(); mountObserver.disconnect();
        const current = currentRoute();
        for (const [link, snapshot] of snapshots) {
            const primary = route(link.getAttribute('href')).params.get('name') === primaryPage;
            let selected = snapshot.selected;
            if (!onHiddenPage()) {
                // React may commit the new route before viewhide. Respect that
                // route rather than restoring the departed page's highlight.
                const mainPage = [...snapshot.section.querySelectorAll('a[href]')].some(candidate => {
                    const target = route(candidate.getAttribute('href'));
                    return target.path === '/configurationpage' && target.params.get('name') === current.params.get('name');
                });
                selected = primary ? current.path === '/configurationpage' && current.params.get('name') === primaryPage
                    : ['/dashboard/plugins', '/dashboard/plugins/repositories'].includes(current.path)
                        || current.path === '/configurationpage' && !mainPage;
            }
            link.classList.toggle('Mui-selected', selected);
            // Restore only the ARIA value this adapter owns; a newer value set
            // by the host or another page must survive cleanup. An old current
            // marker is eligible only while restoring the same route or when
            // this link is selected on the destination route.
            if (link.getAttribute('aria-current') === (primary ? 'page' : null)) {
                if (snapshot.current === null || !onHiddenPage() && !selected) link.removeAttribute('aria-current');
                else link.setAttribute('aria-current', snapshot.current);
            }
        }
        snapshots.clear();
    }, { once: true });
    refresh();
    mountObserver.observe(doc.body, { childList: true, subtree: true });
}
