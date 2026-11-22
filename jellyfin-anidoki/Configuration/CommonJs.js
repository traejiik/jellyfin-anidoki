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
const iconPaths = {
    pencil: ['M16 3a2.1 2.1 0 0 1 3 3L7 18l-4 1 1-4Z', 'm14 5 3 3'], check: ['M20 6 9 17l-5-5'],
    'eye-off': ['M3 3l18 18', 'M10.6 5.1A11 11 0 0 1 12 5c6.5 0 10 7 10 7s-1.1 2.2-3.3 4.1', 'M6.2 6.2C3.4 8.3 2 12 2 12s3.5 7 10 7a11 11 0 0 0 5.8-1.8', 'M10 10a3 3 0 0 0 4 4'],
    eye: ['M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z', 'M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0'],
    'chevron-left': ['m15 6-6 6 6 6'], 'chevron-right': ['m9 6 6 6-6 6'],
    triangle: ['M12 3.75 21 19.5H3Z', 'M12 9.75v4.5', 'M12 17.25h.01']
};
export function icon(name) {
    const namespace = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(namespace, 'svg');
    for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', width: '20', height: '20', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(key, value);
    for (const d of iconPaths[name] ?? iconPaths.eye) { const path = document.createElementNS(namespace, 'path'); path.setAttribute('d', d); svg.append(path); }
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
// A status line that leads with an icon, such as an account's attention line. The words carry the meaning; the icon is hidden from screen readers.
export function statusWithIcon(node, text, tone, iconName) {
    const svg = icon(iconName); svg.setAttribute('width', '14'); svg.setAttribute('height', '14');
    node.replaceChildren(svg, text); node.dataset.tone = tone;
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
// The step before leaving for the tracker's sign-in page, laid out like the disconnect confirmation: what happens, then
// Cancel and Continue. again: a lost sign-in being renewed; userName: the admin linking another user's account.
export function authorizationLink(container, url, name, { again = false, userName, onCancel } = {}) {
    container.className = 'doki-account-detail doki-confirm-inline';
    container.setAttribute('role', 'group'); container.setAttribute('aria-label', `${again ? 'Sign in to' : 'Link'} ${name}`);
    const text = element('p', 'doki-help doki-confirm-text');
    text.append(element('strong', '', again ? `Sign in to ${name} again` : `Link ${name}`),
        userName ? ` Sign in on ${name} with ${userName}’s account and allow AniDōki. You come back here afterwards.`
            : ` Sign in on ${name} and allow AniDōki. You come back here afterwards.`);
    const actions = element('div', 'doki-actions');
    const cancel = element('button', 'doki-button', 'Cancel'); cancel.type = 'button';
    cancel.addEventListener('click', () => onCancel?.());
    const link = element('a', 'doki-button doki-button-primary', `Continue to ${name}`);
    // Same tab: the callback page links back here, and the settings page reloads the linked accounts.
    link.href = safeExternalUrl(url); link.rel = 'noreferrer';
    actions.append(cancel, link);
    container.replaceChildren(text, actions);
    link.focus?.();
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
        // Where the rail is a row of pills (up to 50rem), the header and the sticky pills cover about 144px, where a section
        // reached from a pill lands just below.
    }, { rootMargin: `-${globalThis.matchMedia?.('(max-width: 50rem)').matches ? 160 : 90}px 0px -55% 0px` });
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

// The Completion percentage row (D3). value: the saved choice: null follows the admin's default, 0 is player exit, or a
// percentage; choices: the percentages the server allows; fallsBack: the saved percentage is above the server's, so the
// default applies. serverDefault: the admin's default (null for player exit), whose option reads "(default)".
// userName names the user on the admin's panel; without it the wording speaks to the signed-in user. server: the admin's
// own default row, where null is player exit.
export function thresholdSetting({ value, maxResumePct = 90, choices, fallsBack = false, userName, serverDefault = null, server = false, onChange, signal }) {
    const id = server ? 'doki-threshold-server' : `doki-threshold-${userName ? 'admin' : 'user'}`;
    const row = element('div', 'doki-setting-row doki-threshold-row');
    const text = element('div', 'doki-setting-text');
    const title = element('strong', '', server ? 'Default completion percentage' : 'Completion percentage'); title.id = `${id}-title`;
    const help = element('span', 'doki-help', server
        ? 'How much of an episode users watch before their trackers are updated, unless they choose their own. Percentages update them during playback. Jellyfin’s own watched mark doesn’t change.'
        : userName
        ? `How much of an episode ${userName} watches before their trackers are updated. Percentages update them during playback. Jellyfin’s own watched mark doesn’t change.`
        : 'How much of an episode you watch before your trackers count it as watched. Percentages update them during playback. Jellyfin’s own watched mark doesn’t change.');
    help.id = `${id}-help`;
    text.append(title, help);
    if (fallsBack) {
        const note = element('span', 'doki-help', userName
            ? `${userName} chose ${value}%, which is above this server’s ${maxResumePct}%, so the default applies.`
            : `You chose ${value}%, which is above this server’s ${maxResumePct}%, so the default applies.`);
        text.append(note);
    }
    const exitValue = '0';
    const defaultValue = server ? exitValue : choices.includes(serverDefault) ? String(serverDefault) : exitValue;
    const label = (percent, optionValue) => server
        ? (optionValue === exitValue ? `${percent}% (Jellyfin default)` : `${percent}%`)
        : (optionValue === defaultValue ? `${percent}% (default)` : `${percent}%`);
    const select = element('select', 'doki-input doki-setting-select');
    select.id = id; select.setAttribute('aria-labelledby', title.id); select.setAttribute('aria-describedby', help.id);
    const exit = element('optgroup'); exit.label = 'When the player exits';
    const standard = element('option', '', label(maxResumePct, exitValue)); standard.value = exitValue; exit.append(standard);
    const during = element('optgroup'); during.label = 'During playback';
    for (const percent of choices) { const option = element('option', '', label(percent, String(percent))); option.value = String(percent); during.append(option); }
    select.append(exit, during);
    select.value = server ? (choices.includes(value) ? String(value) : exitValue)
        : value == null || fallsBack ? defaultValue : String(value);
    select.addEventListener('change', () => {
        // The admin's player exit is null. A user who picks the default's option follows the default from then on.
        if (server) onChange(select.value === exitValue ? null : Number(select.value));
        else onChange(select.value === defaultValue ? null : Number(select.value));
    }, { signal });
    row.append(text, select);
    return row;
}

// summaries: provider key → { lastUpdate, attention, signInAgain, replaceToken }. A linked row shows lastUpdate as its second line
// and attention in its status line until a Test result replaces it. signInAgain (the user's own page, a rejected sign-in) puts
// Sign in again in Test's place; replaceToken (the admin's panel, a rejected Annict token) puts Replace token there.
export function renderAccounts(container, { linked, visible, admin = false, userName, action, summaries = {} }, signal) {
    container.replaceChildren();
    for (const provider of providers.filter(item => visible.includes(item.key) || linked.includes(item.key))) {
        const connected = linked.includes(provider.key);
        const row = element('div', 'doki-account-row');
        const summary = connected ? summaries[provider.key] ?? {} : {};
        const title = element('div', 'doki-row-name');
        if (connected) {
            const heading = element('div', 'doki-row-title'); heading.append(element('strong', '', provider.name), element('span', 'doki-help', 'Linked'));
            title.append(heading);
            if (summary.lastUpdate) title.append(element('div', 'doki-help', summary.lastUpdate));
        } else {
            title.append(element('strong', '', provider.name), element('div', 'doki-help', provider.key === 'Kitsu' ? 'Direct sign-in' : provider.key === 'Annict' ? 'Personal token' : 'Not linked'));
        }
        row.append(providerIcon(provider), title);
        const feedback = element('p', 'doki-status'); feedback.setAttribute('role', 'status');
        if (summary.attention) statusWithIcon(feedback, summary.attention, 'attention', 'triangle');
        const detail = element('div', 'doki-account-detail'); detail.hidden = true;
        detail.id = `doki-${admin ? 'admin' : 'user'}-${provider.key}-detail`;
        const buttons = [];
        // Resolves true when the action succeeded, so a saved token can be tested straight after.
        const run = async (kind, credentials = {}) => {
            buttons.forEach(button => button.disabled = true); status(feedback, 'Working…');
            try {
                const result = await action(kind, provider.key, credentials);
                if (signal.aborted) return false;
                if (result?.authorizationUrl) {
                    status(feedback, '');
                    authorizationLink(detail, result.authorizationUrl, provider.name, { again: connected, userName: admin ? userName : undefined, onCancel: closeLink });
                    detail.hidden = false;
                }
                else status(feedback, result?.message ?? (kind === 'test' ? 'Connection verified.' : 'Account updated.'), 'success');
                return true;
            } catch (error) {
                const message = await errorMessage(error);
                if (!signal.aborted) status(feedback, message, 'error');
                return false;
            }
            finally { if (!signal.aborted) buttons.forEach(button => button.disabled = false); }
        };
        const closeLink = () => {
            detail.hidden = true; detail.replaceChildren(); detail.className = 'doki-account-detail';
            detail.removeAttribute('role'); detail.removeAttribute('aria-label');
        };
        const button = (label, handler) => {
            const node = element('button', 'doki-button', label); node.type = 'button'; node.addEventListener('click', handler, { signal }); buttons.push(node); return node;
        };
        // Kitsu's sign-in and Annict's token are typed here rather than on the tracker's site.
        const credentialForm = ({ heading, submitLabel = 'Link account', after } = {}) => {
            detail.replaceChildren();
            const credentials = {};
            const fields = element('div', 'doki-account-fields');
            for (const [key, label, type] of provider.key === 'Kitsu' ? [['Username', 'Kitsu email', 'email'], ['Password', 'Kitsu password', 'password']] : [['Token', heading ?? `Annict token for ${userName}`, 'password']]) {
                const field = element('label', 'doki-field', label); const input = element('input', 'doki-input'); input.type = type; input.autocomplete = 'off'; field.append(input); fields.append(field); credentials[key] = input;
            }
            detail.append(fields);
            if (provider.key === 'Annict') detail.append(element('p', 'doki-help', 'The token is kept in the AniDōki database on the server, not in the plugin configuration.'));
            const actions = element('div', 'doki-actions');
            const link = button(submitLabel, async () => {
                const values = Object.fromEntries(Object.entries(credentials).map(([key, input]) => [key, input.value]));
                if (Object.values(values).some(value => !value)) { status(feedback, 'Fill in all account fields.', 'error'); return; }
                try { if (await run('link', values) && after && !signal.aborted) await after(); } finally { for (const input of Object.values(credentials)) if (input.type === 'password') input.value = ''; }
            });
            link.classList.add('doki-button-primary');
            actions.append(link, button('Close', () => { detail.replaceChildren(); detail.hidden = true; }));
            detail.append(actions); detail.hidden = false;
            Object.values(credentials)[0]?.focus?.();
            signal.addEventListener('abort', () => { detail.replaceChildren(); }, { once: true });
        };
        if (connected) {
            if (summary.signInAgain && provider.key !== 'Annict') {
                const again = button('Sign in again', () => provider.key === 'Kitsu' ? credentialForm({ submitLabel: 'Sign in' }) : run('link'));
                again.classList.add('doki-button-primary'); row.append(again);
            } else if (summary.replaceToken && provider.key === 'Annict') {
                // The token is saved, then tested, so the row says straight away whether Annict accepts it.
                const replace = button('Replace token', () => credentialForm({ heading: `New Annict token for ${userName}`, submitLabel: 'Save token', after: () => run('test') }));
                replace.classList.add('doki-button-primary'); row.append(replace);
            } else {
                row.append(button('Test', () => run('test')));
            }
            let disconnect = null;
            const closeConfirm = () => {
                detail.hidden = true; detail.replaceChildren(); detail.className = 'doki-account-detail';
                detail.removeAttribute('role'); detail.removeAttribute('aria-label');
                disconnect.setAttribute('aria-expanded', 'false'); disconnect.focus();
            };
            disconnect = button('Disconnect', () => {
                if (disconnect.getAttribute('aria-expanded') === 'true') { closeConfirm(); return; }
                const whose = admin ? ` for ${userName}` : '';
                detail.className = 'doki-account-detail doki-confirm-inline';
                detail.setAttribute('role', 'group'); detail.setAttribute('aria-label', `Confirm disconnecting ${provider.name}${whose}`);
                const text = element('p', 'doki-help doki-confirm-text');
                text.append(element('strong', '', `Disconnect ${provider.name}${whose}?`),
                    ` AniDōki stops updating it straight away; ${admin ? `${userName} must sign in again to relink` : 'your list is not changed'}.`);
                const actions = element('div', 'doki-actions');
                const keep = button('Keep linked', closeConfirm);
                const confirm = button(`Disconnect ${provider.name}`, () => run('disconnect')); confirm.classList.add('doki-button-danger');
                actions.append(keep, confirm);
                detail.replaceChildren(text, actions); detail.hidden = false;
                disconnect.setAttribute('aria-expanded', 'true'); keep.focus();
            });
            disconnect.setAttribute('aria-controls', detail.id); disconnect.setAttribute('aria-expanded', 'false');
            detail.addEventListener('keydown', event => {
                if (event.key === 'Escape' && disconnect.getAttribute('aria-expanded') === 'true') { event.preventDefault(); closeConfirm(); }
            }, { signal });
            row.append(disconnect);
        } else if (provider.key === 'Annict' && !admin) {
            row.append(element('span', 'doki-help', 'Ask your administrator to link a personal token.'));
        } else if (provider.key === 'Kitsu' || provider.key === 'Annict') {
            row.append(button('Link', () => credentialForm()));
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
