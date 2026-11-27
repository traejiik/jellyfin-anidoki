import { followThemeAccent, matchesSettingsRequest, popupBackground } from './common.js';
import { providerName } from './outcome-lines.js';
import { createPopupLibrary } from './popup-library.js';
import {
    buildRows, defaultRow, rowTargets, editableTargets, savedState, pillSummary, allSummary, seasonName, airedLabel, seriesStatusLabel, entryUrl, faceNames, editorModel,
    setProgress, setStatus, setScore, markCompleted, isDirty, syncButtons, planSyncUp, planSyncDown, applyEditResults, forceDraft, draftStore, anyDirty
} from './popup-state.js';

// The progress popup (Phase 6, item 6.4): an item-page button, a dialog over the page and the series level.
//
// Checked on jellytest (Jellyfin 12.4) on 2026-11-24: the detail-button row is `.itemDetailPage:not(.hide) .mainDetailButtons`,
// with Favourite as `.btnUserRating` and ⋮ as `.btnMoreCommands`, each a `button-flat detailButton emby-button` around
// `.detailButton-content > .detailButton-icon`. Pushing `?anidoki=<id>` before the hash, and stepping back from it, fires no
// viewshow or hashchange, and a page loaded with the parameter keeps it, so the deep link lives in the search part where
// Jellyfin's hash router never looks.

const runtimeKey = '__anidokiProgressPopup';
const parameter = 'anidoki';
const libraryLevel = 'library';
const pluginPagesLinks = '.pluginMenuOptions a[href], a[data-plugin-pages][href]';
const libraryCacheMs = 10 * 60_000;
const closeFallbackMs = 250;
const readCacheMs = 24 * 60 * 60_000;
const unsettledReadMs = 30_000;
const readCacheLimit = 64;
const focusable = 'button, a[href], input, select, textarea, summary, [tabindex="0"]';

export function startProgressPopup(win = window, doc = document) {
    win[runtimeKey]?.dispose();
    const lifetime = new AbortController(), signal = lifetime.signal;
    const availability = new Map(), links = [];
    let readCache = null, libraryJourney = null;
    let popup = null, disposed = false, ready = false, bootTimer = null, bootAttempts = 0;

    const api = () => win.ApiClient;
    function node(tag, className, text) {
        const element = doc.createElement(tag);
        if (className) element.className = className;
        if (text !== undefined && text !== null) element.textContent = text;
        return element;
    }
    function button(className, text, label) {
        const element = node('button', className, text);
        element.type = 'button';
        if (label) element.setAttribute('aria-label', label);
        return element;
    }
    const assetVersion = () => globalThis.anidokiAssetVersion ??= Date.now().toString(36);
    const assetUrl = name => api().getUrl(`AniDoki/assets/v/${assetVersion()}/${name}`);
    const imageUrl = (id, type, size) => api().getUrl(`Items/${id}/Images/${type}`, size);

    // Jellyfin's ajax wrapper ignores AbortSignal, so requests use fetch with its headers, as the notifications do.
    async function request(path, { method = 'GET', body, abort } = {}) {
        const headers = { Accept: 'application/json' };
        api().setRequestHeaders?.(headers);
        if (body !== undefined)
            headers['Content-Type'] = 'application/json';
        const response = await win.fetch(api().getUrl(path), {
            method, headers, credentials: 'same-origin', cache: 'no-store', signal: abort, body: body === undefined ? undefined : JSON.stringify(body)
        });
        let data = null;
        if (response.status !== 204) {
            try {
                data = await response.json();
            } catch {
                data = null;
            }
        }
        return { ok: response.ok, status: response.status, data };
    }

    function currentReadCache() {
        const client = api();
        const user = client?.isLoggedIn?.() && client.getCurrentUserId?.();
        const server = user && client.getUrl('AniDoki/user/progress');
        if (user && readCache?.user === user && readCache.server === server && readCache.client === client)
            return readCache;

        readCache = null;
        if (!user)
            return null;

        const cache = { user, server, client, entries: new Map(), writer: null, skipUntil: 0,
            library: { results: new Map(), fresh: new Set(), revision: 0 } };
        readCache = cache;
        return cache;
    }

    const seriesPath = current => `AniDoki/user/progress/series/${encodeURIComponent(current.itemId)}`;
    const cacheAvailable = cache => cache && !cache.writer && cache.skipUntil <= Date.now();

    function cachedEntry(cache, path) {
        if (!cacheAvailable(cache))
            return null;
        const entry = cache.entries.get(path);
        if (entry && entry.expires <= Date.now()) {
            cache.entries.delete(path);
            return null;
        }
        return entry;
    }

    function rememberRead(cache, path, entry) {
        const entries = cache.entries;
        entries.delete(path);
        entries.set(path, entry);
        if (entries.size > readCacheLimit)
            entries.delete(entries.keys().next().value);
    }

    // Pending reads belong to the signed-in browsing session so a quick reopen can join them.
    function readPopupData(current, path) {
        const cache = current.cache;
        if (!cacheAvailable(cache) || cache !== currentReadCache())
            return request(path, { abort: current.controller.signal });
        const previous = cachedEntry(cache, path);
        if (previous)
            return previous.pending ?? Promise.resolve(previous.result);
        const entry = { expires: Date.now() + unsettledReadMs };
        entry.pending = request(path, { abort: signal }).then(result => {
            if (cache.entries.get(path) === entry) {
                if (result.ok && result.data) {
                    entry.result = result;
                    entry.expires = Date.now() + readCacheMs;
                    delete entry.pending;
                } else {
                    cache.entries.delete(path);
                }
            }
            return result;
        }, error => {
            if (cache.entries.get(path) === entry)
                cache.entries.delete(path);
            throw error;
        });
        rememberRead(cache, path, entry);
        return entry.pending;
    }

    function invalidateLibrary(cache) {
        if (!cache)
            return;
        cache.library.results.clear();
        cache.library.fresh.clear();
        cache.library.revision++;
    }

    function beginWrite(current) {
        if (!current.cache)
            return;
        current.cache.entries.clear();
        current.cache.writer = current;
        invalidateLibrary(current.cache);
    }

    function endWrite(current, settled) {
        const cache = current.cache;
        if (cache?.writer !== current)
            return;
        cache.writer = null;
        // A queued operation can still run on the server after this popup stops polling.
        cache.skipUntil = settled ? 0 : Date.now() + unsettledReadMs;
        invalidateLibrary(cache);
    }

    // --- Location ----------------------------------------------------------------------------------------------

    const requested = () => new URL(win.location.href).searchParams.get(parameter);
    function withoutParameter() {
        const url = new URL(win.location.href);
        url.searchParams.delete(parameter);
        return url.pathname + url.search + url.hash;
    }
    function detailsItemId() {
        const hash = win.location.hash ?? '', query = hash.indexOf('?');
        return hash.startsWith('#/details') && query >= 0 ? new URLSearchParams(hash.slice(query + 1)).get('id') : null;
    }

    // --- Item-page button ----------------------------------------------------------------------------------------

    function available(itemId) {
        if (!availability.has(itemId)) {
            const check = request(`AniDoki/user/progress/series/${encodeURIComponent(itemId)}/available`, { abort: signal })
                .then(result => {
                    // Only a definite answer is kept; a network failure asks again on the next visit.
                    if (result.status !== 204 && result.status !== 404)
                        availability.delete(itemId);
                    return result.status === 204;
                }, () => {
                    availability.delete(itemId);
                    return false;
                });
            availability.set(itemId, check);
        }
        return availability.get(itemId);
    }

    function itemButton() {
        const element = button('button-flat detailButton emby-button doki-item-button', null, 'AniDōki: this series on your trackers');
        element.setAttribute('is', 'emby-button');
        element.setAttribute('aria-haspopup', 'dialog');
        element.title = 'AniDōki';
        const content = node('div', 'detailButton-content'), icon = node('span', 'material-icons detailButton-icon doki-item-button-icon');
        icon.setAttribute('aria-hidden', 'true');
        content.append(icon);
        element.append(content);
        element.addEventListener('click', () => open(element.dataset.itemId, { opener: element, fromItemPage: true }), { signal });
        return element;
    }

    async function scanPage() {
        const itemId = detailsItemId();
        if (disposed || !ready)
            return;
        // Jellyfin reuses a detail page for the next item, so a button left from the previous one would open the wrong series.
        for (const stale of doc.querySelectorAll('.doki-item-button'))
            if (stale.dataset.itemId !== itemId)
                stale.remove();
        if (!itemId || !api()?.isLoggedIn?.())
            return;
        if (!await available(itemId) || disposed || detailsItemId() !== itemId)
            return;
        const row = doc.querySelector('.itemDetailPage:not(.hide) .mainDetailButtons');
        if (!row)
            return;
        const existing = row.querySelector('.doki-item-button') ?? row.insertBefore(itemButton(), row.querySelector('.btnMoreCommands'));
        existing.dataset.itemId = itemId;
    }

    // --- Dialog --------------------------------------------------------------------------------------------------

    function pushPopupRoute(itemId, viaLibrary, libraryRootPushed) {
        const url = new URL(win.location.href);
        url.searchParams.set(parameter, itemId);
        win.history.pushState({ ...win.history.state, anidoki: itemId, pushed: true, viaLibrary, libraryRootPushed },
            '', url.pathname + url.search + url.hash);
    }

    function openDiscordLink() {
        const url = new URL(win.location.href);
        const [route, query] = url.hash.split('?');
        if (route !== '#/details')
            return false;
        const params = new URLSearchParams(query);
        const itemId = params.get(parameter);
        if (!itemId || !/^[0-9a-f]{32}$/i.test(itemId) || itemId !== params.get('id'))
            return false;

        params.delete(parameter);
        url.hash = `${route}?${params}`;
        win.history.replaceState(win.history.state, '', url.pathname + url.search + url.hash);
        // The Discord URL is a Jellyfin details page with both popup levels above it.
        pushPopupRoute(libraryLevel, false, false);
        pushPopupRoute(itemId, true, true);
        open(itemId, { opener: null, push: false });
        return true;
    }

    function open(itemId, { opener = doc.activeElement, fromItemPage = false, push = true } = {}) {
        if (!itemId || disposed || !ready || (popup?.itemId === itemId && !popup.closing))
            return;
        const previous = popup;
        const insideLibrary = previous?.itemId === libraryLevel && !previous.closing && itemId !== libraryLevel;
        const historyState = win.history.state;
        const ownsEntry = !push && historyState?.anidoki === itemId && historyState.pushed === true;
        const viaLibrary = insideLibrary || (!push && itemId !== libraryLevel && historyState?.anidoki === itemId && historyState.viaLibrary === true);
        const libraryRootPushed = insideLibrary ? previous.pushed : viaLibrary && historyState.libraryRootPushed;
        const cache = currentReadCache();
        if ((itemId === libraryLevel && push && !previous?.viaLibrary) || libraryJourney?.cache !== cache)
            libraryJourney = null;
        const keepShell = previous && !previous.closing && (insideLibrary || itemId === libraryLevel);
        if (previous)
            finish(previous, false, keepShell);
        const current = { itemId, opener: keepShell ? previous.opener : opener, fromItemPage,
            controller: new AbortController(), view: null, rows: [], rowKey: null, drafts: {}, openProvider: null,
            pushed: ownsEntry, viaLibrary, libraryRootPushed, closing: false, removed: false, timer: null, menuOpen: false,
            openOnMenu: false, motion: null, busy: false, store: null, shell: keepShell ? previous.shell : null,
            cards: {}, notes: {}, timers: new Set(), detailReads: {}, matchView: null, cache };
        popup = current;
        if (push && requested() !== itemId) {
            pushPopupRoute(itemId, viaLibrary, libraryRootPushed);
            current.pushed = true;
        }
        if (itemId === libraryLevel) {
            buildLibrary(current, Boolean(keepShell));
            return;
        }
        build(current, Boolean(keepShell));
        const cached = cachedEntry(current.cache, seriesPath(current))?.result;
        if (cached)
            receive(current, cached.data);
        else {
            loadSummary(current);
            load(current);
        }
    }

    function stepBack(current) {
        if (current.viaLibrary)
            win.history.back();
        else
            close();
    }

    function showLibrary(current) {
        if (current.viaLibrary) {
            stepBack(current);
            return;
        }
        const url = new URL(win.location.href);
        url.searchParams.set(parameter, libraryLevel);
        win.history.replaceState({ ...win.history.state, anidoki: libraryLevel, pushed: current.pushed,
            viaLibrary: false, libraryRootPushed: false }, '', url.pathname + url.search + url.hash);
        open(libraryLevel, { push: false });
    }

    function clearRoute() {
        const state = { ...win.history.state };
        delete state.anidoki;
        delete state.pushed;
        delete state.viaLibrary;
        delete state.libraryRootPushed;
        win.history.replaceState(state, '', withoutParameter());
    }

    function close({ fromHistory = false, replace = false } = {}) {
        const current = popup;
        if (!current || current.closing)
            return;
        current.closing = true;
        current.controller.abort();
        if (!fromHistory && requested()) {
            if (current.viaLibrary && !replace) {
                if (current.libraryRootPushed)
                    win.history.go(-2);
                else {
                    // Close strips the deep-linked library entry. Forward now reopens a standalone series.
                    win.history.replaceState({ ...win.history.state, viaLibrary: false, libraryRootPushed: false }, '', win.location.href);
                    current.clearLibraryOnBack = true;
                    win.history.back();
                }
            } else if (current.pushed && !replace) {
                win.history.back();
            } else {
                clearRoute();
            }
        }
        current.scrim.classList.add('is-closing');
        current.timer = win.setTimeout(() => finish(current, true), closeFallbackMs);
    }

    function finish(current, restoreFocus, keepShell = false) {
        if (current.removed)
            return;
        current.removed = true;
        endWrite(current, false);
        current.controller.abort();
        win.clearTimeout(current.timer);
        for (const id of current.timers)
            win.clearTimeout(id);
        current.timers.clear();
        if (!keepShell) {
            current.shell.exit?.cancel();
            current.shell.controller.abort();
            current.scrim.remove();
            current.shell.inertRoot?.removeAttribute('inert');
            doc.documentElement.classList?.remove('doki-popup-open');
        }
        if (popup === current)
            popup = null;
        if (restoreFocus) {
            const target = current.opener?.isConnected ? current.opener : doc.body;
            target?.focus?.({ preventScroll: true });
        }
    }

    function trapFocus(current, event) {
        const items = [...current.panel.querySelectorAll(focusable)].filter(item => !item.hidden && !item.closest('[hidden]') && item.getAttribute('tabindex') !== '-1');
        if (!items.length)
            return;
        const first = items[0], last = items.at(-1), active = doc.activeElement;
        if (event.shiftKey && (active === first || !current.panel.contains(active))) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (active === last || !current.panel.contains(active))) {
            event.preventDefault();
            first.focus();
        }
    }

    function pluginPagesSettingsUrl(link, settings) {
        const url = new URL(link.getAttribute('href'), win.location.href);
        const [route, query] = url.hash.split('?');
        if (url.origin !== settings.origin || route !== '#/userpluginsettings.html')
            return null;
        const pageUrl = new URLSearchParams(query).get('pageUrl')?.toLowerCase();
        return pageUrl === settings.pathname.toLowerCase() || pageUrl === '/anidoki/settings' ? url : null;
    }

    async function openFromPluginPages(event) {
        if (!ready || !api()?.isLoggedIn?.() || event.defaultPrevented || (event.button ?? 0) !== 0 ||
            event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
            return;
        const link = event.target?.closest?.(pluginPagesLinks);
        const target = link?.getAttribute('target');
        if (!link || (target && target !== '_self'))
            return;
        const settings = new URL(api().getUrl('AniDoki/settings'), win.location.href);
        if (!pluginPagesSettingsUrl(link, settings))
            return;

        event.preventDefault();
        event.stopPropagation();
        const href = win.location.href;
        const itemId = detailsItemId();
        let opener = link;
        if (link.closest('#app-user-menu')) {
            opener = doc.querySelector('[aria-controls="app-user-menu"]') ?? link;
            // Jellyfin's account menu traps focus until its Escape handler dismisses it.
            link.dispatchEvent(new win.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
            await new Promise(resolve => win.requestAnimationFrame(resolve));
        }
        const eligible = itemId && await available(itemId);
        if (!disposed && href === win.location.href)
            open(eligible ? itemId : libraryLevel, { opener, fromItemPage: Boolean(eligible) });
    }

    function settingsHref(sourceKey) {
        const settings = new URL(api().getUrl('AniDoki/settings'), win.location.href);
        const links = doc.querySelectorAll(pluginPagesLinks);
        const menuUrl = [...links].map(link => pluginPagesSettingsUrl(link, settings)).find(Boolean);
        const url = menuUrl ?? new URL(win.location.href);
        url.searchParams.delete(parameter);
        if (!sourceKey && menuUrl)
            return url.href;

        const [route = '#/userpluginsettings.html', query] = menuUrl ? url.hash.split('?') : [];
        const params = new URLSearchParams(query ?? { pageUrl: '/AniDoki/settings' });
        if (sourceKey) {
            params.set('dokiSection', 'trackerMatchesTitle');
            params.set('dokiSource', sourceKey);
        }
        url.hash = `${route}?${params}`;
        return url.href;
    }

    function bindSettingsLink(current, link, sourceKey) {
        link.setAttribute('href', settingsHref(sourceKey));
        link.addEventListener('click', event => {
            if (event.defaultPrevented || (event.button ?? 0) !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
                return;
            event.preventDefault();
            const destination = settingsHref(sourceKey);
            try {
                const session = storage();
                if (sourceKey)
                    session?.setItem(matchesSettingsRequest, JSON.stringify({ sourceKey, expires: Date.now() + 60_000 }));
                else
                    session?.removeItem(matchesSettingsRequest);
            } catch {
                // The route also carries the destination when storage is unavailable.
            }
            win.history.replaceState(win.history.state ?? null, '', withoutParameter());
            finish(current, false);
            win.location.assign(destination);
        }, { signal: current.controller.signal });
    }

    function chip(tag, className, label, icon) {
        const element = tag === 'a' ? node('a', `doki-popup-chip ${className}`) : button(`doki-popup-chip ${className}`);
        element.setAttribute('aria-label', label);
        element.title = label;
        const glyph = node('span', 'doki-popup-chip-icon');
        glyph.dataset.icon = icon;
        glyph.setAttribute('aria-hidden', 'true');
        element.append(glyph);
        return element;
    }

    function popupHeader(current, isLibrary = false) {
        const signal = current.controller.signal;
        const header = node('header', 'doki-popup-header'), start = node('div', 'doki-popup-header-start');
        const symbol = node('span', 'doki-symbol');
        symbol.setAttribute('aria-hidden', 'true');
        const crumbs = node('nav', 'doki-popup-crumbs');
        crumbs.setAttribute('aria-label', 'Breadcrumb');
        const crumb = node(isLibrary ? 'h2' : 'span', 'doki-popup-crumb', isLibrary ? 'Your anime' : 'AniDōki');
        crumb.setAttribute('aria-current', 'page');
        if (!isLibrary) {
            const back = button('doki-popup-library-back', 'Your anime');
            back.addEventListener('click', () => showLibrary(current), { signal });
            const separator = node('span', 'doki-popup-crumb-divider', '/');
            separator.setAttribute('aria-hidden', 'true');
            crumbs.append(back, separator);
        }
        crumbs.append(crumb);
        const unsaved = node('p', 'doki-popup-unsaved', 'Unsaved changes');
        unsaved.hidden = true;
        const titleRow = node('div', 'doki-popup-crumb-row');
        titleRow.append(symbol, crumbs);
        start.append(titleRow, unsaved);
        const actions = node('div', 'doki-popup-actions');
        const openOn = node('div', 'doki-popup-open-on');
        const settings = chip('a', 'doki-popup-settings', 'AniDōki settings', 'gear');
        bindSettingsLink(current, settings);
        const closeButton = chip('button', 'doki-popup-close', 'Close', 'close');
        closeButton.addEventListener('click', () => close(), { signal });
        actions.append(openOn, settings, closeButton);
        header.append(start, actions);

        return { header, crumb, unsaved, openOn };
    }

    function build(current, transition = false) {
        const signal = current.controller.signal;
        const { header, crumb, unsaved, openOn } = popupHeader(current);
        const body = node('div', 'doki-popup-body');
        const hero = node('div', 'doki-popup-hero');
        const layout = node('div', 'doki-popup-layout');
        const side = node('aside', 'doki-popup-side');
        const poster = node('div', 'doki-popup-poster');
        const details = node('dl', 'doki-popup-details');
        details.setAttribute('aria-label', 'Series details');
        details.hidden = true;
        const detailsSkeleton = node('div', 'doki-popup-details doki-details-skeleton');
        detailsSkeleton.setAttribute('aria-hidden', 'true');
        detailsSkeleton.hidden = true;
        for (let row = 0; row < 4; row++)
            detailsSkeleton.append(node('span', 'doki-skeleton-line'), node('span', 'doki-skeleton-line'));
        const seasons = node('div', 'doki-popup-seasons');
        seasons.hidden = true;
        side.append(poster, details, detailsSkeleton, seasons);
        const main = node('div', 'doki-popup-main');
        const titleBlock = node('div', 'doki-popup-title');
        const logo = node('img', 'doki-popup-logo');
        logo.alt = '';
        logo.hidden = true;
        const title = node('h2', '', 'AniDōki');
        title.id = 'dokiPopupTitle';
        title.setAttribute('tabindex', '-1');
        titleBlock.append(logo, title);
        const meta = node('p', 'doki-popup-meta');
        const seasonMenu = node('div', 'doki-popup-season-menu');
        seasonMenu.hidden = true;
        const tabs = node('div', 'doki-popup-tabs');
        tabs.setAttribute('role', 'tablist');
        tabs.setAttribute('aria-label', 'Sections');
        const overviewTab = button('doki-popup-tab', 'Overview');
        const matchesTab = button('doki-popup-tab', 'Matches');
        const overview = node('div', 'doki-popup-panel');
        const matches = node('div', 'doki-popup-panel');
        const related = node('section', 'doki-popup-related');
        related.hidden = true;
        overview.dataset.region = 'overview';
        const panels = [[overviewTab, overview, 'dokiPopupOverview'], [matchesTab, matches, 'dokiPopupMatches']];
        function selectTab(next, focus = false) {
            for (const [tab, content] of panels) {
                const active = tab === next;
                tab.setAttribute('aria-selected', String(active));
                tab.setAttribute('tabindex', active ? '0' : '-1');
                content.hidden = !active;
            }
            if (focus)
                next.focus();
        }
        for (const [tab, content, id] of panels) {
            tab.id = `${id}Tab`;
            tab.setAttribute('role', 'tab');
            tab.setAttribute('aria-controls', id);
            content.id = id;
            content.setAttribute('role', 'tabpanel');
            content.setAttribute('aria-labelledby', tab.id);
            content.setAttribute('tabindex', '0');
            tab.addEventListener('click', () => selectTab(tab, true), { signal });
            tab.addEventListener('keydown', event => {
                const next = { ArrowLeft: tab === overviewTab ? matchesTab : overviewTab,
                    ArrowRight: tab === overviewTab ? matchesTab : overviewTab, Home: overviewTab, End: matchesTab }[event.key];
                if (next) {
                    event.preventDefault();
                    selectTab(next, true);
                }
            }, { signal });
            tabs.append(tab);
        }
        selectTab(overviewTab);
        const status = node('div', 'doki-popup-status');
        status.setAttribute('role', 'status');
        main.append(titleBlock, meta, tabs, status, overview, matches);
        layout.append(side, main);
        body.append(hero, layout);
        Object.assign(current, { crumb, unsaved, openOn, hero, poster, details, detailsSkeleton, seasons, logo, title, meta, seasonMenu, overview, matches, related, status });
        mountLevel(current, header, body, title, transition);
    }

    function mountLevel(current, header, body, title, transition) {
        if (!current.shell) {
            const scrim = node('div', 'anidoki doki-popup-scrim');
            const panel = node('section', 'doki-popup');
            const controller = new AbortController();
            const signal = controller.signal;
            panel.setAttribute('role', 'dialog');
            panel.setAttribute('aria-modal', 'true');
            panel.setAttribute('aria-labelledby', 'dokiPopupTitle');
            const background = popupBackground(doc, win);
            const backgroundRoot = doc.getElementById('reactRoot');
            const inertRoot = backgroundRoot && !backgroundRoot.hasAttribute('inert') ? backgroundRoot : null;
            inertRoot?.setAttribute('inert', '');
            current.shell = { scrim, panel, background, inertRoot, controller, body };
            const shell = current.shell;
            panel.addEventListener('keydown', event => {
                const active = popup;
                if (!active || active.closing)
                    return;
                if (event.key === 'Escape') {
                    event.preventDefault();
                    if (active.libraryUi?.closeMenu(true))
                        return;
                    if (active.menuOpen)
                        closeSeasonMenu(active, true);
                    else if (active.openOnMenu) {
                        active.openOnMenu = false;
                        refresh(active, 'open-on');
                    } else
                        stepBack(active);
                } else if (event.key === 'Tab') {
                    trapFocus(active, event);
                }
            }, { signal });
            panel.addEventListener('animationend', event => {
                if (event.target === panel && popup?.closing)
                    finish(popup, true);
            }, { signal });
            for (const type of ['wheel', 'touchmove'])
                scrim.addEventListener(type, event => {
                    if (!shell.body.contains(event.target))
                        event.preventDefault();
                }, { passive: false, signal });
            doc.documentElement.classList?.add('doki-popup-open');
            scrim.append(panel);
            doc.body.append(scrim);
            followThemeAccent(scrim, signal, win);
        }
        const shell = current.shell;
        const { scrim, panel, background } = shell;
        Object.assign(current, { scrim, panel });
        shell.exit?.cancel();
        shell.exit = null;
        const show = () => {
            if (current !== popup || current.closing)
                return;
            shell.body = body;
            panel.classList.toggle('is-library', current.itemId === libraryLevel);
            if (transition)
                body.classList.add('doki-popup-level-enter');
            panel.replaceChildren(...[background, header, body].filter(Boolean));
            if (transition && current.libraryUi)
                current.libraryUi.restoreFocus();
            else
                title.focus({ preventScroll: true });
        };
        if (transition && shell.body.animate && win.matchMedia?.('(prefers-reduced-motion: no-preference)').matches) {
            shell.body.setAttribute('inert', '');
            const exit = shell.exit = shell.body.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, easing: 'ease', fill: 'forwards' });
            exit.finished.then(show, () => {});
        } else {
            show();
        }
    }

    function buildLibrary(current, transition) {
        const { header, crumb } = popupHeader(current, true);
        crumb.id = 'dokiPopupTitle';
        crumb.setAttribute('tabindex', '-1');
        const body = node('div', 'doki-popup-body doki-popup-library');
        const state = libraryJourney ??= {
            cache: current.cache, type: 'all', status: 'all', provider: null, query: '', layout: 'posters',
            searchOpen: false, view: null, busy: false, error: null
        };
        current.libraryState = state;
        current.libraryUi = createPopupLibrary({
            root: body, state, title: crumb, node, button, chip, imageUrl, providerMark: key => providerTile(key),
            openSeries: id => open(id), selectProvider: key => {
                if (key !== state.provider)
                    loadLibrary(current, key);
            },
            refresh: () => loadLibrary(current, state.provider, true),
            signal: current.controller.signal
        });
        mountLevel(current, header, body, crumb, transition);
        loadLibrary(current, state.provider, false, transition);
    }

    async function loadLibrary(current, provider, refresh = false, restore = false) {
        if (current.removed || current.closing)
            return;
        const state = current.libraryState, ui = current.libraryUi;
        const cache = currentReadCache();
        if (current.identityTimer != null) {
            win.clearTimeout(current.identityTimer);
            current.timers.delete(current.identityTimer);
            current.identityTimer = null;
        }
        if (!current.cache && cache)
            current.cache = state.cache = cache;
        const library = cache === current.cache ? cache?.library : null;
        if (!library) {
            // On a fresh web load ApiClient is present before Jellyfin restores its user session.
            if (!cache && !current.cache && !refresh && (current.identityChecks ?? 0) < 8) {
                current.identityChecks = (current.identityChecks ?? 0) + 1;
                state.busy = true;
                ui.render();
                current.identityTimer = win.setTimeout(() => loadLibrary(current, provider, false, restore), 250);
                current.timers.add(current.identityTimer);
                return;
            }
            Object.assign(state, { provider: null, view: null, busy: false, error: 'Sign in to see your anime.' });
            ui.render();
            return;
        }
        current.libraryRequest?.abort();
        const key = provider ?? '';
        const snapshot = library.results.get(key);
        const reuse = !refresh && cacheAvailable(cache) && snapshot?.expires > Date.now();
        const revision = library.revision;
        if (reuse) {
            Object.assign(state, { view: snapshot.view, provider: snapshot.view.selectedProvider, busy: false, refreshing: false, error: null });
            ui.render();
            if (restore)
                ui.restoreFocus();
            return;
        }

        const controller = new AbortController(), requestSignal = controller.signal;
        current.libraryRequest = controller;
        const levelSignal = current.controller.signal;
        const abort = () => controller.abort();
        levelSignal.addEventListener('abort', abort, { once: true });
        const previousView = state.view;
        const sameProvider = previousView?.selectedProvider === provider;
        Object.assign(state, { provider, busy: true, refreshing: refresh, error: null,
            view: sameProvider ? previousView : previousView && { ...previousView, selectedProvider: null, rows: [] } });
        ui.render();
        const params = new URLSearchParams();
        if (provider)
            params.set('provider', provider);
        const afterChange = revision > 0 && !library.fresh.has(key);
        if (refresh || afterChange)
            params.set('refresh', 'true');
        if (afterChange)
            params.set('afterChange', 'true');
        const query = params.size ? '?' + params : '';
        const isCurrent = () => current === popup && !current.closing && !requestSignal.aborted &&
            current.libraryRequest === controller && library.revision === revision && currentReadCache() === cache;
        try {
            const result = await request('AniDoki/user/progress/library' + query, { abort: requestSignal });
            if (!isCurrent())
                return;
            if (!result.ok || !result.data)
                throw result;
            const view = result.data;
            const selected = view.selectedProvider;
            if (cacheAvailable(cache) && (selected || !provider)) {
                const entry = { view, expires: Date.now() + libraryCacheMs };
                library.results.set(key, entry);
                library.fresh.add(key);
                if (selected) {
                    library.results.set(selected, entry);
                    library.fresh.add(selected);
                }
                if (library.results.get('')?.view.selectedProvider === selected)
                    library.results.set('', entry);
            }
            Object.assign(state, { view, provider: selected });
        } catch (error) {
            if (!isCurrent())
                return;
            if (error?.status === 401 || error?.status === 404) {
                // Access failures are authoritative, even for a previously successful tracker.
                library.results.clear();
                library.fresh.clear();
                if (error.status === 401)
                    cache.entries.clear();
                state.view = null;
            }
            state.error = error?.status === 401 ? 'Sign in to see your anime.'
                : error?.status === 404 ? 'Your anime is unavailable on this server.'
                : 'Could not read the tracker list.';
        } finally {
            levelSignal.removeEventListener('abort', abort);
            if (isCurrent()) {
                Object.assign(state, { busy: false, refreshing: false });
                ui.render();
                if (restore)
                    ui.restoreFocus();
            }
        }
    }

    async function loadSummary(current) {
        try {
            const result = await readPopupData(current, `${seriesPath(current)}/summary`);
            if (result.ok && result.data?.name && current === popup && !current.closing && !current.view)
                renderSeriesHeader(current, result.data);
        } catch {
            // The full view also carries the artwork and title.
        }
    }

    async function load(current) {
        const skeleton = node('div', 'doki-overview-skeleton');
        skeleton.setAttribute('aria-hidden', 'true');
        const description = node('div', 'doki-skeleton-description');
        description.append(node('span', 'doki-skeleton-line'), node('span', 'doki-skeleton-line'));
        const pills = node('div', 'doki-pills');
        pills.append(node('span', 'doki-tracker-skeleton-pill'), node('span', 'doki-tracker-skeleton-pill'));
        skeleton.append(description, pills);
        current.overview.replaceChildren(skeleton);
        current.matches.replaceChildren(matchesSkeleton());
        for (const panel of [current.overview, current.matches])
            panel.setAttribute('aria-busy', 'true');
        current.status.replaceChildren(node('p', 'doki-sr-only', 'Loading tracker progress…'));
        current.status.hidden = false;
        current.detailsSkeleton.hidden = false;
        try {
            const result = await readPopupData(current, seriesPath(current));
            if (current !== popup || current.closing)
                return;
            if (!result.ok || !result.data)
                throw result;
            receive(current, result.data);
        } catch {
            if (current !== popup || current.closing)
                return;
            current.overview.replaceChildren();
            current.matches.replaceChildren();
            const retry = button('doki-button doki-popup-retry', 'Try again');
            retry.addEventListener('click', () => load(current), { signal });
            current.status.replaceChildren(node('p', 'doki-popup-error', "Couldn't load this series"), retry);
        } finally {
            for (const panel of [current.overview, current.matches])
                panel.removeAttribute('aria-busy');
            current.detailsSkeleton.hidden = true;
        }
    }

    function matchesSkeleton() {
        const skeleton = node('div', 'doki-matches-skeleton');
        skeleton.setAttribute('aria-hidden', 'true');
        const intro = node('div', 'doki-skeleton-description');
        intro.append(node('span', 'doki-skeleton-line'));
        skeleton.append(intro);
        for (let index = 0; index < 2; index++) {
            const group = node('div', 'doki-popup-match-group');
            const header = node('div', 'doki-popup-match-summary');
            header.append(node('span', 'doki-popup-match-chevron'), node('span', 'doki-skeleton-line doki-match-skeleton-logo'),
                node('span', 'doki-skeleton-line doki-match-skeleton-name'));
            const row = node('div', 'doki-popup-match-row');
            for (let cell = 0; cell < 4; cell++)
                row.append(node('span', 'doki-skeleton-line'));
            group.append(header, row);
            skeleton.append(group);
        }
        return skeleton;
    }

    function storage() {
        try {
            return win.sessionStorage ?? null;
        } catch {
            return null;
        }
    }

    /** Takes a fresh view: its rows, a row that still exists, and only the drafts that still differ from the trackers. */
    function adopt(current, view) {
        current.view = view;
        current.rows = buildRows(view);
        if (!current.rows.some(row => row.key === current.rowKey))
            current.rowKey = defaultRow(view, current.rows)?.key ?? null;
        let pruned = false;
        for (const row of current.rows) {
            const drafts = current.drafts[row.key];
            if (!drafts)
                continue;
            const next = { ...drafts };
            for (const target of editableTargets(rowTargets(view, row)))
                if (next[target.provider] && !isDirty(target, next[target.provider])) {
                    delete next[target.provider];
                    pruned = true;
                }
            if (Object.keys(next).length)
                current.drafts[row.key] = next;
            else
                delete current.drafts[row.key];
        }
        if (pruned)
            current.store?.save(current.drafts);
    }

    function receive(current, view) {
        if (!current.store) {
            // D12: pending edits survive closing the popup and reloading the tab, per user and series.
            current.store = draftStore(storage(), api().getCurrentUserId?.() ?? '', view.id);
            current.drafts = current.store.load();
        }
        adopt(current, view);
        current.status.replaceChildren();
        current.status.hidden = true;
        render(current);
    }

    // --- Series level ----------------------------------------------------------------------------------------------

    const selectedRow = current => current.rows.find(row => row.key === current.rowKey) ?? null;

    function rowCount(current, row) {
        const targets = editableTargets(rowTargets(current.view, row)), drafts = current.drafts[row.key] ?? {};
        if (!targets.length)
            return '';
        const open = targets.find(target => target.provider === current.openProvider);
        if (open)
            return pillSummary(open, drafts[open.provider]).text;
        return targets.length > 1 ? allSummary(targets, drafts).text : pillSummary(targets[0], drafts[targets[0].provider]).text;
    }

    function render(current) {
        const view = current.view;
        current.unsaved.hidden = !anyDirty(current.drafts);
        renderSeriesHeader(current, view);
        renderDetails(current);
        renderSeasons(current);
        renderHeaderLinks(current);
        renderOverview(current);
        renderMatches(current);
        loadSeriesDetails(current);
    }

    function renderSeriesHeader(current, view) {
        renderArtwork(current, view);
        renderMeta(current, view);
    }

    function renderArtwork(current, view) {
        current.crumb.textContent = view.name;
        current.title.textContent = view.name;
        if (current.artworkId === view.id)
            return;
        current.artworkId = view.id;
        const images = new Set(view.images ?? []);
        current.hero.style.backgroundImage = images.has('Backdrop')
            ? `url("${imageUrl(view.id, 'Backdrop', { maxWidth: 1600 })}")` : '';
        current.poster.replaceChildren();
        if (images.has('Primary')) {
            const img = node('img', '');
            img.alt = '';
            img.src = imageUrl(view.id, 'Primary', { maxHeight: 552 });
            current.poster.append(img);
        }
        current.logo.hidden = true;
        current.title.classList.remove('doki-sr-only');
        if (images.has('Logo')) {
            current.logo.addEventListener('load', () => {
                current.logo.hidden = false;
                current.title.classList.add('doki-sr-only');
            }, { once: true, signal: current.controller.signal });
            current.logo.src = imageUrl(view.id, 'Logo', { maxWidth: 720 });
        }
    }

    function renderMeta(current, view) {
        const items = [];
        if (view.year)
            items.push(node('span', 'doki-popup-meta-item', view.endYear && view.endYear !== view.year ? `${view.year}–${view.endYear}` : `${view.year}`));
        if (view.communityRating) {
            const rating = node('span', 'doki-popup-meta-item doki-popup-community', `★ ${view.communityRating.toFixed(1)}`);
            rating.setAttribute('aria-label', `Community rating ${view.communityRating.toFixed(1)}`);
            items.push(rating);
        }
        if (view.officialRating)
            items.push(node('span', 'doki-popup-meta-item doki-popup-official', view.officialRating));
        const status = seriesStatusLabel(view.status);
        if (status)
            items.push(node('span', 'doki-popup-meta-item', status));
        current.meta.replaceChildren(...items);
    }

    function renderDetails(current) {
        const view = current.view, row = selectedRow(current);
        const targets = row ? editableTargets(rowTargets(view, row)) : [];
        const known = current.rows.length === 1 ? targets.find(target => target.entry.totalEpisodes > 0)?.entry.totalEpisodes : null;
        const facts = [['Format', view.isMovie ? 'Movie' : 'TV'], ['Episodes', `${known ?? view.episodeCount}`], ['Aired', airedLabel(view.premiereDate)],
            ['Status', seriesStatusLabel(view.status)]].filter(([, value]) => value);
        const items = facts.flatMap(([name, value]) => [node('dt', '', name), node('dd', '', value)]);
        const airing = current.detailReads.airing?.data;
        const date = new Date(airing?.airsAtUtc ?? NaN);
        if (airing?.episode > 0 && date.getTime() > Date.now()) {
            const day = new Intl.DateTimeFormat(undefined, { weekday: 'long' }).format(date);
            const next = node('dd', '', `Episode ${airing.episode}`);
            next.append(node('span', 'doki-popup-airing-date', new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(date)));
            items.push(node('dt', '', 'Airs'), node('dd', '', day), node('dt', '', 'Next'), next);
        }
        current.details.replaceChildren(...items);
        current.details.hidden = false;
    }

    function seasonOption(current, row) {
        const option = node('div', 'doki-popup-season');
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', String(row.key === current.rowKey));
        option.setAttribute('tabindex', row.key === current.rowKey ? '0' : '-1');
        option.dataset.rowKey = row.key;
        option.classList.toggle('is-split', row.split);
        const first = editableTargets(rowTargets(current.view, row))[0];
        option.title = row.heading ?? first?.entry.title ?? row.label;
        const dot = node('span', 'doki-popup-dot');
        dot.hidden = !Object.keys(current.drafts[row.key] ?? {}).length;
        dot.setAttribute('aria-label', 'Unsaved changes');
        option.append(node('span', 'doki-popup-season-label', row.label), dot, node('span', 'doki-popup-season-count', rowCount(current, row)));
        option.addEventListener('click', () => selectRow(current, row.key, true), { signal });
        return option;
    }

    function seasonList(current, label) {
        const list = node('div', 'doki-popup-season-list');
        list.setAttribute('role', 'listbox');
        list.setAttribute('aria-label', label);
        let group = null;
        for (const row of current.rows) {
            if (row.split && row.group !== group) {
                const heading = node('div', 'doki-popup-season-group', row.group);
                heading.setAttribute('role', 'presentation');
                list.append(heading);
            }
            group = row.split ? row.group : null;
            list.append(seasonOption(current, row));
        }
        list.addEventListener('keydown', event => {
            const options = [...list.querySelectorAll('[role=option]')], index = options.indexOf(doc.activeElement);
            const move = { ArrowDown: index + 1, ArrowUp: index - 1, Home: 0, End: options.length - 1 }[event.key];
            if (move !== undefined) {
                event.preventDefault();
                const next = options[Math.max(0, Math.min(options.length - 1, move))];
                for (const option of options)
                    option.setAttribute('tabindex', option === next ? '0' : '-1');
                next?.focus();
            } else if ((event.key === 'Enter' || event.key === ' ') && index >= 0) {
                event.preventDefault();
                selectRow(current, options[index].dataset.rowKey, true);
            }
        }, { signal });
        return list;
    }

    function renderSeasons(current) {
        const several = current.rows.length > 1;
        current.seasons.hidden = !several;
        current.seasonMenu.hidden = !several;
        if (!several) {
            current.seasons.replaceChildren();
            current.seasonMenu.replaceChildren();
            return;
        }
        current.seasons.replaceChildren(node('h3', 'doki-popup-box-title', 'Seasons'), seasonList(current, 'Seasons'));
        current.seasons.querySelector('[aria-selected=true]')?.scrollIntoView?.({ block: 'nearest' });
        // Below 900px the box becomes a "Season" menu: a button and a listbox, not a native select (D6).
        const row = selectedRow(current);
        const toggle = button('doki-popup-season-button');
        toggle.setAttribute('aria-haspopup', 'listbox');
        toggle.setAttribute('aria-expanded', String(current.menuOpen));
        toggle.append(node('span', 'doki-popup-season-button-label', 'Season'), node('span', 'doki-popup-season-label', row?.split ? `${row.group} · ${row.label}` : row?.label ?? ''),
            node('span', 'doki-popup-season-count', row ? rowCount(current, row) : ''));
        toggle.addEventListener('click', () => current.menuOpen ? closeSeasonMenu(current, false) : openSeasonMenu(current), { signal });
        const list = seasonList(current, 'Season');
        list.hidden = !current.menuOpen;
        current.seasonMenu.replaceChildren(toggle, list);
    }

    function openSeasonMenu(current) {
        current.menuOpen = true;
        renderSeasons(current);
        current.seasonMenu.querySelector('[aria-selected=true]')?.focus();
    }

    function closeSeasonMenu(current, refocus) {
        current.menuOpen = false;
        renderSeasons(current);
        if (refocus)
            current.seasonMenu.querySelector('.doki-popup-season-button')?.focus();
    }

    function selectRow(current, key, keepFocus) {
        if (!current.rows.some(row => row.key === key))
            return;
        const fromMenu = current.menuOpen;
        current.rowKey = key;
        current.menuOpen = false;
        render(current);
        if (keepFocus)
            (fromMenu ? current.seasonMenu.querySelector('.doki-popup-season-button') : current.seasons.querySelector('[aria-selected=true]'))?.focus();
    }

    function renderHeaderLinks(current) {
        const view = current.view, fromSeriesPage = current.fromItemPage && current.itemId === view.id;
        const links = trackerLinks(current);
        if (!fromSeriesPage) {
            const jellyfin = chip('a', 'doki-popup-open-jellyfin', 'Open in Jellyfin', 'jellyfin');
            jellyfin.setAttribute('href', `#/details?id=${encodeURIComponent(view.id)}`);
            jellyfin.addEventListener('click', () => close({ replace: true }), { signal });
            links.push(jellyfin);
        }
        current.openOn.replaceChildren(...links);
    }

    /** Open on <tracker>: follows the expanded pill; with none or All trackers open, a menu of the linked trackers. */
    function trackerLinks(current) {
        const row = selectedRow(current);
        const targets = row ? editableTargets(rowTargets(current.view, row)) : [];
        const linkTo = (target, element) => {
            element.setAttribute('href', entryUrl(target.provider, target.entry));
            element.setAttribute('target', '_blank');
            element.setAttribute('rel', 'noopener');
            return element;
        };
        const single = targets.find(target => target.provider === current.openProvider) ?? (targets.length === 1 ? targets[0] : null);
        if (single)
            return [linkTo(single, chip('a', 'doki-popup-open-tracker', `Open on ${providerName(single.provider)}`, 'external'))];
        if (!targets.length)
            return [];
        const wrap = node('div', 'doki-popup-menu-wrap');
        const toggle = chip('button', 'doki-popup-open-tracker', 'Open on tracker', 'dropdown');
        toggle.setAttribute('aria-haspopup', 'menu');
        toggle.setAttribute('aria-expanded', String(current.openOnMenu));
        toggle.dataset.focusKey = 'open-on';
        toggle.addEventListener('click', () => {
            current.openOnMenu = !current.openOnMenu;
            refresh(current, 'open-on');
        }, { signal });
        const menu = node('div', 'doki-popup-menu');
        menu.setAttribute('role', 'menu');
        menu.hidden = !current.openOnMenu;
        for (const target of targets) {
            const item = linkTo(target, node('a', 'doki-popup-menu-item'));
            item.setAttribute('role', 'menuitem');
            item.append(providerTile(target.provider, false), node('span', '', providerName(target.provider)));
            item.addEventListener('click', () => {
                current.openOnMenu = false;
                refresh(current);
            }, { signal });
            menu.append(item);
        }
        wrap.append(toggle, menu);
        return [wrap];
    }

    function renderOverview(current) {
        const view = current.view, row = selectedRow(current);
        current.overview.replaceChildren();
        if (view.overview)
            current.overview.append(node('p', 'doki-popup-description', view.overview));
        // D10: below 900px the Season menu sits after the description and before the pills; CSS hides it on wider screens.
        current.overview.append(current.seasonMenu);
        if (!row) {
            current.overview.append(node('p', 'doki-popup-loading', 'No seasons to track'));
            return;
        }
        if (row.heading)
            current.overview.append(node('p', 'doki-popup-row-heading', row.heading));
        current.overview.append(renderPills(current, row), current.related);
    }

    function loadSeriesDetails(current) {
        const row = selectedRow(current);
        const season = row?.split ? current.view.seasons.find(item => item.sourceKey === row.sourceKey) : null;
        const targets = row ? editableTargets(rowTargets(current.view, row)).flatMap(target => {
            const tracker = season?.trackers.find(item => item.provider === target.provider);
            if (!tracker?.matchRanges)
                return [target];
            const range = tracker.matchRanges.find(item => item.sourceStart <= row.firstEpisode && item.sourceEnd >= row.firstEpisode);
            return range ? [{ ...target, entry: range.entry }] : [];
        }) : [];
        const preferred = targets.find(target => target.provider === current.openProvider) ?? targets[0];
        const aniList = targets.find(target => target.provider === 'AniList');
        const path = `AniDoki/user/progress/series/${encodeURIComponent(current.view.id)}`;
        const sourceKey = row?.sourceKey;
        const related = preferred ? { provider: preferred.provider, sourceKey, entryId: preferred.entry.id } : null;
        const firstPart = row && current.rows.find(item => item.sourceKey === sourceKey) === row;
        const airing = row && (aniList || firstPart) ? { sourceKey, ...(aniList && { aniListEntryId: aniList.entry.id }) } : null;
        readSeriesDetail(current, 'related', related && `${path}/related?${new URLSearchParams(related)}`, 'relations', renderRelated, related?.provider);
        readSeriesDetail(current, 'airing', airing && `${path}/airing?${new URLSearchParams(airing)}`, 'airing', renderDetails);
    }

    async function readSeriesDetail(current, kind, path, field, renderDetail, provider) {
        const previous = current.detailReads[kind];
        if ((previous?.path ?? null) === (path ?? null) && previous?.view === current.view)
            return;
        const cached = path && cachedEntry(current.cache, path)?.result;
        const read = { view: current.view, path, provider, data: cached?.data?.[field] ?? null };
        current.detailReads[kind] = read;
        renderDetail(current);
        if (!path || cached)
            return;

        try {
            const result = await readPopupData(current, path);
            if (result.ok && current === popup && !current.closing && !current.removed && current.detailReads[kind] === read) {
                read.data = result.data?.[field] ?? null;
                renderDetail(current);
            }
        } catch {
            // Supplemental reads leave the editor usable on failure.
        }
    }

    const relationLabels = {
        Sequel: 'Sequel', Prequel: 'Prequel', SideStory: 'Side story',
        AlternativeVersion: 'Alternative version', AlternativeSetting: 'Alternative setting', Other: 'Related'
    };

    function renderRelated(current) {
        const relations = current.detailReads.related?.data;
        if (!Array.isArray(relations) || !relations.length) {
            current.related.hidden = true;
            current.related.replaceChildren();
            return;
        }
        const list = node('ul', 'doki-popup-relations');
        for (const relation of relations) {
            const provider = current.detailReads.related.provider;
            const href = relation.entryId ? entryUrl(provider, { id: relation.entryId }) : null;
            const item = node('li', 'doki-popup-relation');
            const card = node(href ? 'a' : 'div', 'doki-popup-relation-card');
            const art = node('span', 'doki-popup-relation-art');
            if (relation.coverUrl?.startsWith('https://')) {
                const image = node('img');
                image.src = relation.coverUrl;
                image.alt = '';
                image.loading = 'lazy';
                image.referrerPolicy = 'no-referrer';
                art.append(image);
            }
            const text = node('div', 'doki-popup-relation-text');
            const title = node('span', 'doki-popup-relation-title', relation.title || 'Untitled entry');
            if (href) {
                card.setAttribute('href', href);
                card.setAttribute('target', '_blank');
                card.setAttribute('rel', 'noopener noreferrer');
                title.id = `dokiRelatedTitle${list.children.length}`;
                card.setAttribute('aria-labelledby', title.id);
                const logo = node('span', 'doki-popup-relation-logo');
                logo.setAttribute('aria-hidden', 'true');
                logo.style.setProperty('--doki-relation-logo', `url("${assetUrl(providerLogos[provider] ?? 'anidoki-symbol.svg')}")`);
                art.append(logo);
            }
            text.append(node('span', 'doki-popup-secondary', relationLabels[relation.kind] ?? 'Related'), title);
            const seasons = [...new Set((relation.libraryMatches ?? []).map(match => match.season))];
            for (const season of seasons.length ? seasons : [undefined]) {
                const label = season === undefined ? 'Not in your library' : season === null ? 'In your library'
                    : `In your library (${seasonName(season)})`;
                text.append(node('span', 'doki-popup-secondary', label));
            }
            card.append(art, text);
            item.append(card);
            list.append(item);
        }
        current.related.replaceChildren(node('h3', '', 'Related'), list);
        current.related.hidden = false;
    }

    const matchOrigins = { Automatic: 'Automatic', Shared: 'Shared default', Personal: 'Your override' };
    const matchEpisodes = (first, last) => first === last ? `Episode ${first}` : `Episodes ${first}–${last}`;

    function renderMatches(current) {
        const sourceKey = selectedRow(current)?.sourceKey;
        if (current.matchView === current.view && current.matchSourceKey === sourceKey)
            return;
        current.matchView = current.view;
        current.matchSourceKey = sourceKey;
        const { view, matches } = current;
        const expanded = new Map([...matches.querySelectorAll('details')].map(group => [group.dataset.provider, group.open]));
        const intro = node('p', 'doki-popup-match-intro', 'Which entry each linked tracker uses for this series, and how your library’s episodes map to it. ');
        if (view.matchesLinkAvailable && sourceKey) {
            const link = node('a', 'doki-popup-link', 'Your matches in AniDōki settings');
            link.dataset.focusKey = 'matches:settings';
            bindSettingsLink(current, link, sourceKey);
            intro.append(node('span', '', 'To create or change your own matches, go to '), link);
        } else {
            intro.append(node('span', '', 'Your server admin manages these matches.'));
        }
        const groups = view.trackers.map(({ provider }) => {
            const sources = view.seasons.map(season => ({ season, tracker: season.trackers.find(item => item.provider === provider) }));
            const matched = sources.filter(({ tracker }) => tracker?.matchRanges?.length);
            const unavailable = sources.some(({ tracker }) => tracker?.error);
            const group = node('details', 'doki-popup-match-group');
            group.dataset.provider = provider;
            group.open = expanded.get(provider) ?? (matched.length > 0);
            const header = node('summary', 'doki-popup-match-summary');
            header.dataset.focusKey = `matches:${provider}`;
            const type = view.isMovie ? 'movie' : 'season';
            const count = `${matched.length} ${type}${matched.length === 1 ? '' : 's'}`;
            const chevron = node('span', 'doki-popup-match-chevron');
            chevron.setAttribute('aria-hidden', 'true');
            header.append(chevron, providerTile(provider, false), node('strong', '', providerName(provider)), node('span', 'doki-popup-secondary', count));
            group.append(header);
            const rows = node('ul', 'doki-popup-match-rows');
            for (const { season, tracker } of matched)
                for (const match of tracker.matchRanges)
                    rows.append(renderMatchRow(view, season, tracker, match));
            if (unavailable || !matched.length)
                rows.append(node('li', 'doki-popup-match-message doki-popup-secondary',
                    unavailable ? 'Some matches could not be read.' : 'No matches for this tracker.'));
            group.append(rows);
            return group;
        });
        const focused = doc.activeElement;
        const focusKey = matches.contains(focused) ? focused.dataset.focusKey : null;
        matches.replaceChildren(intro, ...groups);
        if (focusKey)
            (matches.querySelector(`[data-focus-key="${focusKey}"]`) ?? matches).focus({ preventScroll: true });
    }

    function renderMatchRow(view, season, tracker, match) {
        const row = node('li', 'doki-popup-match-row');
        const source = node('div', 'doki-popup-match-cell');
        source.append(node('strong', '', view.isMovie ? 'Movie' : seasonName(season.season)),
            node('span', 'doki-popup-secondary', matchEpisodes(match.sourceStart, match.sourceEnd)));
        const direction = node('span', 'doki-popup-secondary', '→');
        direction.setAttribute('aria-hidden', 'true');
        const target = node('div', 'doki-popup-match-cell');
        target.append(node('span', '', match.entry.title), node('span', 'doki-popup-secondary', matchEpisodes(match.targetStart, match.targetEnd)));
        const origin = node('span', 'doki-popup-match-origin', matchOrigins[tracker.origin]);
        origin.dataset.origin = tracker.origin;
        row.append(source, direction, target, origin);
        return row;
    }

    // --- Overview: pills and the editor ------------------------------------------------------------------------------

    const providerLogos = {
        AniList: 'provider-anilist.svg', Mal: 'provider-mal.svg', Kitsu: 'provider-kitsu.svg', Annict: 'provider-annict.png',
        Shikimori: 'provider-shikimori.svg', Simkl: 'provider-simkl.svg'
    };
    const rowDrafts = (current, row) => current.drafts[row.key] ?? {};
    const percent = (value, total) => total > 0 ? Math.max(0, Math.min(100, value / total * 100)) : 0;
    const pillName = key => key === 'all' ? 'All trackers' : providerName(key);

    function setRowDrafts(current, row, drafts) {
        if (Object.keys(drafts).length)
            current.drafts[row.key] = drafts;
        else
            delete current.drafts[row.key];
        current.store?.save(current.drafts);
    }

    function providerTile(provider, dirty) {
        const element = node('span', 'doki-pill-tile');
        if (provider === 'all') {
            element.classList.add('is-all');
            const symbol = node('span', 'doki-symbol');
            symbol.setAttribute('aria-hidden', 'true');
            element.append(symbol);
        } else {
            const img = node('img', '');
            img.alt = '';
            img.src = assetUrl(providerLogos[provider] ?? 'anidoki-symbol.svg');
            element.append(img);
        }
        if (dirty) {
            const dot = node('span', 'doki-popup-dot doki-pill-dirty');
            dot.setAttribute('aria-label', 'Unsaved changes');
            element.append(dot);
        }
        return element;
    }

    /** What a pill shows, for one tracker or for All trackers. */
    function pillInfo(current, row, key) {
        const targets = rowTargets(current.view, row), drafts = rowDrafts(current, row);
        if (key === 'all') {
            const editable = editableTargets(targets), model = editorModel(editable, drafts, { all: true }), summary = allSummary(editable, drafts);
            const progress = Math.max(0, ...editable.map(target => (drafts[target.provider] ?? savedState(target)).progress));
            return { key, name: 'All trackers', targets: editable, editable: editable.length > 1, status: summary.status, statusKey: model.fields.status.value ?? 'none',
                text: summary.text, progress, total: model.total ?? 0, dirty: model.dirty };
        }
        const target = targets.find(item => item.provider === key), summary = pillSummary(target, drafts[key]);
        return { key, name: providerName(key), targets: target.state === 'ok' ? [target] : [], editable: target.state === 'ok', status: summary.status,
            statusKey: target.state === 'ok' ? (drafts[key] ?? savedState(target)).status ?? 'none' : target.state, text: summary.text,
            progress: summary.progress, total: summary.total, dirty: target.state === 'ok' && isDirty(target, drafts[key]) };
    }

    function summaryLine(info) {
        const line = node('span', 'doki-pill-line');
        const dot = node('span', 'doki-status-dot');
        dot.dataset.status = info.statusKey;
        dot.setAttribute('aria-hidden', 'true');
        line.append(dot, node('span', 'doki-pill-status', info.status));
        if (info.text) {
            const bar = node('span', 'doki-bar'), fill = node('span', 'doki-bar-fill');
            bar.setAttribute('aria-hidden', 'true');
            fill.style.width = `${percent(info.progress, info.total)}%`;
            bar.append(fill);
            line.append(bar, node('span', 'doki-pill-count', info.text));
        }
        return line;
    }

    function pillContent(info) {
        const text = node('span', 'doki-pill-text');
        text.append(node('strong', 'doki-pill-name', info.name), summaryLine(info));
        return [providerTile(info.key, info.dirty), text];
    }

    function renderPills(current, row) {
        const targets = rowTargets(current.view, row), editable = editableTargets(targets);
        const keys = [...(editable.length > 1 ? ['all'] : []), ...targets.map(target => target.provider)];
        if (current.openProvider && !pillInfo(current, row, current.openProvider)?.editable)
            current.openProvider = null;
        const wrap = node('div', 'doki-pills');
        wrap.setAttribute('role', 'group');
        wrap.setAttribute('aria-label', 'Trackers');
        const motion = current.motion;
        current.motion = null;
        if (motion)
            wrap.classList.add(`is-${motion}`);
        if (!current.openProvider) {
            for (const key of keys) {
                const info = pillInfo(current, row, key);
                const pill = button('doki-pill');
                pill.dataset.focusKey = `pill:${key}`;
                pill.dataset.provider = key;
                pill.setAttribute('aria-expanded', 'false');
                pill.append(...pillContent(info));
                if (!info.editable)
                    pill.setAttribute('aria-disabled', 'true');
                pill.addEventListener('click', () => {
                    if (!info.editable)
                        return;
                    current.openProvider = key;
                    current.motion = 'opening';
                    refresh(current, `pill:${key}`);
                }, { signal });
                wrap.append(pill);
            }
            return wrap;
        }
        wrap.classList.add('is-expanded');
        const tiles = node('div', 'doki-pill-tiles');
        for (const key of keys.filter(item => item !== current.openProvider)) {
            const info = pillInfo(current, row, key);
            const tile = button('doki-pill-icon');
            tile.dataset.focusKey = `tile:${key}`;
            const label = `${info.name}: ${info.status}${info.text ? `, ${info.text}` : ''}`;
            tile.setAttribute('aria-label', label);
            tile.title = label;
            tile.append(providerTile(key, info.dirty));
            if (!info.editable)
                tile.setAttribute('aria-disabled', 'true');
            tile.addEventListener('click', () => {
                if (!info.editable)
                    return;
                current.openProvider = key;
                current.motion = 'switching';
                refresh(current, `pill:${key}`);
            }, { signal });
            tiles.append(tile);
        }
        wrap.append(tiles, openPill(current, row, pillInfo(current, row, current.openProvider)));
        return wrap;
    }

    function openPill(current, row, info) {
        const all = info.key === 'all', drafts = rowDrafts(current, row), model = editorModel(info.targets, drafts, { all });
        // While a sync runs the editor is locked, so an edit made meanwhile cannot be dropped by the result.
        model.locked = model.readOnly || current.busy;
        const section = node('section', 'doki-pill-open');
        section.dataset.provider = info.key;
        const head = button('doki-pill doki-pill-head');
        head.dataset.focusKey = `pill:${info.key}`;
        head.setAttribute('aria-expanded', 'true');
        head.append(...pillContent(info));
        if (model.mixedNote)
            head.append(node('span', 'doki-pill-difference', model.mixedNote));
        const chevron = node('span', 'doki-pill-chevron');
        chevron.setAttribute('aria-hidden', 'true');
        head.append(chevron);
        head.addEventListener('click', () => {
            current.openProvider = null;
            current.motion = 'closing';
            refresh(current, `pill:${info.key}`);
        }, { signal });
        section.append(head);
        if (model.readOnlyReason) {
            const warning = node('div', 'doki-banner doki-pill-note', model.readOnlyReason);
            warning.dataset.icon = 'triangle';
            section.append(warning);
        }
        // Safari does not focus a clicked button, so each control names the focus key to restore after the re-render.
        const edit = (change, focusKey) => {
            if (current.busy)
                return;
            setRowDrafts(current, row, change(rowDrafts(current, row)));
            delete current.notes[row.key];
            refresh(current, focusKey);
        };
        section.append(fieldsRow(info, model, edit), progressRow(info, model, edit), ...cardsFor(current, row, info), footer(current, row, info, model, edit));
        return section;
    }

    function field(label, ...controls) {
        const wrap = node('div', 'doki-editor-field');
        wrap.append(node('span', 'doki-editor-field-label', label), ...controls);
        return wrap;
    }

    function fieldsRow(info, model, edit) {
        const row = node('div', 'doki-editor-row doki-editor-fields'), disabled = model.locked;
        const select = node('select', 'doki-select');
        select.dataset.focusKey = 'status';
        select.setAttribute('aria-label', 'Status');
        const value = model.fields.status.value;
        const choices = value === null ? [['', 'Not on your list'], ...model.statuses] : model.statuses;
        for (const [key, label] of choices) {
            const option = node('option', '', label);
            option.value = key;
            if (key === (value ?? ''))
                option.selected = true;
            if (key === '' || key === 'Mixed')
                option.disabled = true;
            select.append(option);
        }
        select.value = value ?? '';
        select.disabled = disabled;
        select.addEventListener('change', () => edit(drafts => setStatus(info.targets, drafts, select.value), 'status'), { signal });
        row.append(field('Status', select));
        const progress = model.fields.progress;
        if (progress) {
            const stepper = node('div', 'doki-stepper');
            const minus = button('doki-step', null, 'One episode fewer'), plus = button('doki-step', null, 'One episode more');
            minus.dataset.focusKey = 'minus';
            plus.dataset.focusKey = 'plus';
            minus.dataset.icon = 'minus';
            plus.dataset.icon = 'plus';
            minus.disabled = disabled || progress.value <= 0;
            plus.disabled = disabled || (progress.max !== null && progress.value >= progress.max);
            minus.addEventListener('click', () => edit(drafts => setProgress(info.targets, drafts, progress.value - 1), progress.value - 1 > 0 ? 'minus' : 'plus'), { signal });
            plus.addEventListener('click', () => edit(drafts => setProgress(info.targets, drafts, progress.value + 1), progress.max !== null && progress.value + 1 >= progress.max ? 'minus' : 'plus'), { signal });
            const count = node('span', 'doki-step-value');
            count.setAttribute('aria-live', 'polite');
            count.append(node('strong', '', `${progress.value}`), node('span', 'doki-step-total', ` / ${progress.max ?? '?'}`));
            stepper.append(minus, count, plus);
            row.append(field('Episodes watched', stepper));
        }
        const rating = model.fields.rating;
        if (rating)
            row.append(field('Rating', ratingControl(info, rating, disabled, edit)));
        return row;
    }

    function ratingControl(info, rating, disabled, edit) {
        if (rating.kind === 'number') {
            const wrap = node('span', 'doki-rating-number');
            const input = node('input', 'doki-input');
            input.type = 'number';
            input.dataset.focusKey = 'rating';
            input.setAttribute('aria-label', `Rating out of ${rating.max}`);
            input.min = String(rating.min);
            input.max = String(rating.max);
            input.step = String(rating.step);
            input.inputMode = 'decimal';
            input.value = rating.value === 'Mixed' || rating.value === null ? '' : String(rating.value);
            input.placeholder = rating.value === 'Mixed' ? 'Mixed' : '–';
            input.disabled = disabled;
            input.addEventListener('change', () => {
                const text = input.value.trim(), parsed = text === '' ? null : Number(text);
                if (parsed !== null && !Number.isFinite(parsed))
                    return;
                edit(drafts => setScore(info.targets, drafts, parsed), 'rating');
            }, { signal });
            wrap.append(input, node('span', 'doki-rating-scale', `/ ${rating.max}`));
            return wrap;
        }
        const group = node('span', `doki-rating-choices is-${rating.kind}`);
        group.setAttribute('role', 'group');
        group.setAttribute('aria-label', rating.value === 'Mixed' ? 'Rating: Mixed' : 'Rating');
        for (let value = 1; value <= rating.max; value++) {
            const label = rating.kind === 'stars' ? `${value} of 5 stars` : faceNames[value - 1];
            const choice = button('doki-rating-choice', null, label);
            choice.dataset.focusKey = `rating:${value}`;
            choice.dataset.icon = rating.kind === 'stars' ? 'star' : `face-${value}`;
            const on = rating.kind === 'stars' ? rating.value !== 'Mixed' && rating.value >= value : rating.value === value;
            choice.classList.toggle('is-on', Boolean(on));
            choice.setAttribute('aria-pressed', String(rating.value === value));
            choice.title = label;
            choice.disabled = disabled;
            choice.addEventListener('click', () => edit(drafts => setScore(info.targets, drafts, value, { toggle: true }), `rating:${value}`), { signal });
            group.append(choice);
        }
        return group;
    }

    function progressRow(info, model, edit) {
        const row = node('div', 'doki-editor-row doki-editor-progress');
        const progress = model.fields.progress;
        if (!progress?.max) {
            row.hidden = true;
            return row;
        }
        const track = node('div', 'doki-track');
        const range = node('input', 'doki-range');
        range.type = 'range';
        range.dataset.focusKey = 'bar';
        range.min = '0';
        range.max = String(progress.max);
        range.step = '1';
        range.value = String(progress.value);
        range.disabled = model.locked;
        range.setAttribute('aria-label', 'Episodes watched');
        const updateFill = () => range.style.setProperty('--doki-progress', String(percent(Number(range.value), progress.max) / 100));
        updateFill();
        range.addEventListener('input', updateFill, { signal });
        range.addEventListener('change', () => edit(drafts => setProgress(info.targets, drafts, Number(range.value)), 'bar'), { signal });
        const tick = node('span', 'doki-track-tick');
        tick.setAttribute('aria-hidden', 'true');
        tick.style.setProperty('--doki-progress', String(percent(model.jellyfin, progress.max) / 100));
        track.append(range, tick);
        const key = node('p', 'doki-track-key');
        const mine = node('span', 'doki-track-key-item is-tracker', info.key === 'all' ? 'Your trackers' : info.name);
        const jellyfin = node('span', 'doki-track-key-item is-jellyfin', `Jellyfin: ${model.jellyfin} / ${progress.max}`);
        key.append(mine, jellyfin);
        row.append(track, key);
        return row;
    }

    function cardsFor(current, row, info) {
        const providers = new Set(info.targets.map(target => target.provider));
        return (current.cards[row.key] ?? []).filter(card => providers.has(card.provider)).map(card => {
            const element = node('div', `doki-card is-${card.kind}`);
            element.setAttribute('role', card.kind === 'failed' ? 'alert' : 'status');
            element.append(node('strong', 'doki-card-heading', card.heading), node('span', 'doki-card-body', card.body));
            if (card.actions.length) {
                const actions = node('span', 'doki-card-actions');
                for (const action of card.actions) {
                    const control = button('doki-button doki-card-action', action.label);
                    control.dataset.action = action.id;
                    control.dataset.focusKey = `card:${card.provider}:${action.id}`;
                    control.addEventListener('click', () => cardAction(current, row, card, action.id), { signal });
                    actions.append(control);
                }
                element.append(actions);
            }
            return element;
        });
    }

    function syncButton(current, className, label, state, icon, onClick) {
        const wrap = node('span', 'doki-sync-wrap');
        const control = button(`doki-sync ${className}`);
        control.dataset.focusKey = className;
        const glyph = node('span', 'doki-sync-icon');
        glyph.dataset.icon = icon;
        glyph.setAttribute('aria-hidden', 'true');
        control.append(glyph, node('span', '', label));
        const reason = current.busy ? 'Syncing…' : state.enabled ? '' : state.reason;
        if (reason) {
            // Disabled buttons stay focusable and say why on hover and focus.
            control.setAttribute('aria-disabled', 'true');
            const tip = node('span', 'doki-sync-tooltip', reason);
            tip.id = `dokiTip-${className}`;
            tip.setAttribute('role', 'tooltip');
            control.setAttribute('aria-describedby', tip.id);
            wrap.append(control, tip);
        } else
            wrap.append(control);
        control.addEventListener('click', () => {
            if (control.getAttribute('aria-disabled') !== 'true')
                onClick();
        }, { signal });
        return wrap;
    }

    function footer(current, row, info, model, edit) {
        const wrap = node('div', 'doki-editor-row doki-editor-footer');
        const notes = node('div', 'doki-editor-notes');
        if (model.lowerNote)
            notes.append(node('p', 'doki-editor-warning', model.lowerNote));
        if (model.suggestComplete && !model.locked) {
            const complete = button('doki-button doki-mark-complete', 'Mark Completed');
            complete.dataset.focusKey = 'complete';
            complete.addEventListener('click', () => edit(drafts => markCompleted(info.targets, drafts), 'status'), { signal });
            notes.append(complete);
        }
        const status = node('p', 'doki-editor-status');
        status.setAttribute('role', 'status');
        if (current.busy) {
            const icon = node('span', 'doki-popup-sync-spinner');
            icon.setAttribute('aria-hidden', 'true');
            status.append(icon, node('span', '', 'Syncing…'));
        } else {
            status.textContent = current.notes[row.key] ?? '';
        }
        notes.append(status);
        const buttons = syncButtons(info.targets, rowDrafts(current, row), { all: info.key === 'all' });
        const actions = node('div', 'doki-sync-actions');
        actions.append(syncButton(current, 'doki-sync-up', buttons.up.label, buttons.up, 'up', () => syncUp(current, row, info.targets)),
            syncButton(current, 'doki-sync-down', buttons.down.label, buttons.down, 'down', () => syncDown(current, row, info.targets)));
        wrap.append(notes, actions);
        return wrap;
    }

    // A field commits on blur, which a press on another control causes. Re-rendering then would replace that control
    // before the pointer comes up, and the browser would drop its click; so wait for the click, then render once.
    let pointerHeld = false, heldRefresh = null;
    doc.addEventListener('pointerdown', () => { pointerHeld = true; }, { capture: true, signal });
    const releasePointer = () => {
        if (!pointerHeld)
            return;
        win.setTimeout(() => {
            pointerHeld = false;
            const held = heldRefresh;
            heldRefresh = null;
            if (held)
                refresh(held.current, held.focusKey);
        }, 0);
    };
    doc.addEventListener('pointerup', releasePointer, { capture: true, signal });
    doc.addEventListener('pointercancel', releasePointer, { capture: true, signal });
    win.addEventListener('blur', releasePointer, { signal });

    /** Re-renders the parts an edit changes, keeping focus on the same control. */
    function refresh(current, focusKey = doc.activeElement?.dataset?.focusKey) {
        if (current !== popup || current.removed || !current.view)
            return;
        if (pointerHeld) {
            // The pressed control's edit comes last, so its focus key wins. Safari does not focus a clicked button.
            heldRefresh = { current, focusKey: focusKey ?? heldRefresh?.focusKey };
            return;
        }
        current.unsaved.hidden = !anyDirty(current.drafts);
        renderDetails(current);
        renderSeasons(current);
        renderHeaderLinks(current);
        renderOverview(current);
        renderMatches(current);
        loadSeriesDetails(current);
        if (focusKey)
            current.panel.querySelector(`[data-focus-key="${focusKey}"]`)?.focus({ preventScroll: true });
    }

    // --- Saving and syncing ------------------------------------------------------------------------------------------

    const pollEveryMs = 1000, pollLimit = 60;

    function wait(current, ms) {
        return new Promise(resolve => {
            const id = win.setTimeout(() => {
                current.timers.delete(id);
                resolve();
            }, ms);
            current.timers.add(id);
        });
    }

    /** Polls until done(data) or a minute passes; returns the last data, or null when it gave up. */
    async function poll(current, path, done) {
        for (let attempt = 0; attempt < pollLimit; attempt++) {
            await wait(current, pollEveryMs);
            if (current.controller.signal.aborted)
                return null;
            const result = await request(path, { abort: current.controller.signal });
            if (result.ok && result.data && done(result.data))
                return result.data;
        }
        return null;
    }

    const finished = operation => !['Queued', 'Running'].includes(operation.state);

    async function runSync(current, kind, provider) {
        const body = { itemId: current.view.id, provider };
        let started = await request(`AniDoki/user/progress/${kind}`, { method: 'POST', body, abort: current.controller.signal });
        if (started.status === 409 && started.data?.operationId) {
            // Another sync in this direction is running: wait for it, then try once more.
            await poll(current, `AniDoki/user/operations/${started.data.operationId}`, finished);
            started = await request(`AniDoki/user/progress/${kind}`, { method: 'POST', body, abort: current.controller.signal });
        }
        if (!started.ok || !started.data?.operationId)
            throw started;
        return poll(current, `AniDoki/user/operations/${started.data.operationId}`, finished);
    }

    function failureNote(failure) {
        if (typeof failure?.data === 'string' && failure.data)
            return failure.data;
        return "Couldn't sync. Try again in a moment";
    }

    async function syncUp(current, row, targets) {
        if (current.busy)
            return;
        const plan = planSyncUp(targets, rowDrafts(current, row), { isMovie: current.view.isMovie, itemId: current.view.id });
        const byProvider = Object.fromEntries(targets.map(target => [target.provider, target]));
        current.busy = true;
        beginWrite(current);
        delete current.notes[row.key];
        current.cards[row.key] = (current.cards[row.key] ?? []).filter(card => !byProvider[card.provider]);
        refresh(current);
        let results = [], note = null, settled = true;
        try {
            if (plan.edit) {
                const started = await request('AniDoki/user/progress/edit', { method: 'POST', body: plan.edit, abort: current.controller.signal });
                if (!started.ok || !started.data?.operationId)
                    throw started;
                const outcome = await poll(current, `AniDoki/user/progress/edit/${started.data.operationId}`, data => data.finished);
                if (outcome)
                    results = outcome.results;
                else {
                    settled = false;
                    note = 'Still saving. Check History in AniDōki settings';
                }
            }
            for (const provider of note ? [] : plan.syncs) {
                if (!await runSync(current, 'sync-to-trackers', provider)) {
                    settled = false;
                    note = 'Still syncing. Check History in AniDōki settings';
                    break;
                }
            }
        } catch (failure) {
            settled = false;
            note = failureNote(failure);
        }
        await settle(current, row, results, byProvider, note, settled);
    }

    async function syncDown(current, row, targets) {
        const provider = planSyncDown(targets, rowDrafts(current, row));
        if (current.busy || !provider)
            return;
        current.busy = true;
        beginWrite(current);
        delete current.notes[row.key];
        refresh(current);
        let note = null, settled = true;
        try {
            if (!await runSync(current, 'sync-to-jellyfin', provider)) {
                settled = false;
                note = 'Still syncing. Check History in AniDōki settings';
            }
        } catch (failure) {
            settled = false;
            note = failureNote(failure);
        }
        await settle(current, row, [], {}, note, settled);
    }

    function resultNote(item) {
        const name = providerName(item.provider);
        if (item.result === 'Stale')
            return `${name} changed while this was open. Try again`;
        if (item.result === 'NotFound')
            return `${name} no longer has this entry`;
        return {
            transient: `Couldn't reach ${name}. Try again in a moment`, 'rate-limited': `${name} is busy. Try again in a minute`,
            unauthorized: `${name} needs you to sign in again`
        }[item.failure] ?? `${name} refused the change`;
    }

    /** After a save or sync: read the view again, apply the edit results to the drafts, and show what needs the user. */
    async function settle(current, row, results, byProvider, note, settled) {
        if (current !== popup || current.closing || current.removed)
            return;
        endWrite(current, settled);
        current.busy = false;
        const before = rowDrafts(current, row);
        // A result with no draft behind it (a movie raised to Jellyfin's count) has no card to explain it, so it gets a note.
        const unexplained = results.find(item => !before[item.provider] && ['Stale', 'Failed', 'NotFound'].includes(item.result));
        note ??= unexplained ? resultNote(unexplained) : null;
        const applied = applyEditResults(before, results, byProvider);
        setRowDrafts(current, row, applied.drafts);
        current.cards[row.key] = [...(current.cards[row.key] ?? []), ...applied.cards];
        if (note)
            current.notes[row.key] = note;
        try {
            const fresh = await readPopupData(current, seriesPath(current));
            if (fresh.ok && fresh.data && current === popup && !current.removed)
                adopt(current, fresh.data);
        } catch {
            // The view stays as it was; the next open reads it again.
        }
        refresh(current);
    }

    function cardAction(current, row, card, action) {
        current.cards[row.key] = (current.cards[row.key] ?? []).filter(item => item !== card);
        const drafts = rowDrafts(current, row);
        if (action === 'load' || action === 'undo') {
            const next = { ...drafts };
            delete next[card.provider];
            setRowDrafts(current, row, next);
            refresh(current, `pill:${current.openProvider}`);
            return;
        }
        if (action === 'force')
            setRowDrafts(current, row, forceDraft(drafts, card.provider, card.current ?? {}));
        const target = editableTargets(rowTargets(current.view, row)).find(item => item.provider === card.provider);
        if (target)
            syncUp(current, row, [target]);
    }

    // --- Lifetime --------------------------------------------------------------------------------------------------

    function addStyles() {
        const add = (name, marker) => {
            if (doc.querySelector(`link[${marker}]`))
                return;
            const link = node('link', '');
            link.rel = 'stylesheet';
            link.href = assetUrl(name);
            link.setAttribute(marker, '');
            doc.head.append(link);
            links.push(link);
        };
        // The shared tokens come from styles.css, which the notifications usually load already.
        if (!doc.querySelector('link[data-anidoki-notification-style]'))
            add('styles.css', 'data-anidoki-popup-base');
        add('progress-popup.css', 'data-anidoki-popup-style');
    }

    function bootstrap() {
        if (disposed || ready)
            return;
        if (!api()?.getUrl || !doc.body) {
            if (++bootAttempts < 120)
                bootTimer = win.setTimeout(bootstrap, 500);
            else
                dispose();
            return;
        }
        bootTimer = null;
        ready = true;
        addStyles();
        const deepLink = requested();
        if (deepLink)
            open(deepLink, { opener: null, push: false });
        else
            openDiscordLink();
        scanPage();
    }

    function dispose() {
        if (disposed)
            return;
        disposed = true;
        lifetime.abort();
        readCache = null;
        win.clearTimeout(bootTimer);
        if (popup)
            finish(popup, false);
        for (const element of doc.querySelectorAll('.doki-item-button'))
            element.remove();
        for (const link of links)
            link.remove();
        if (win[runtimeKey]?.dispose === dispose)
            delete win[runtimeKey];
    }

    // The other AniDōki views report saved changes through this event.
    doc.addEventListener('anidoki:progress-changed', () => {
        readCache?.entries.clear();
        invalidateLibrary(readCache);
        if (popup?.libraryUi && popup.cache === readCache)
            loadLibrary(popup, popup.libraryState.provider);
    }, { signal });
    doc.addEventListener('click', openFromPluginPages, { capture: true, signal });
    doc.addEventListener('viewshow', () => scanPage(), { signal });
    win.addEventListener('hashchange', () => scanPage(), { signal });
    win.addEventListener('popstate', () => {
        const wanted = requested();
        if (popup?.closing && popup.clearLibraryOnBack && wanted === libraryLevel) {
            clearRoute();
            return;
        }
        if (wanted && (wanted !== popup?.itemId || popup.closing))
            open(wanted, { opener: popup?.opener ?? null, push: false });
        else if (!wanted && popup && !popup.closing)
            close({ fromHistory: true });
    }, { signal });

    win[runtimeKey] = { open, close, dispose };
    bootstrap();
    return win[runtimeKey];
}
