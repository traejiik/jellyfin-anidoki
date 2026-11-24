import { providerName } from './outcome-lines.js';
import {
    buildRows, defaultRow, rowTargets, editableTargets, savedState, pillSummary, allSummary, airedLabel, seriesStatusLabel, entryUrl, faceNames, editorModel,
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
const closeFallbackMs = 250;
const focusable = 'button, a[href], input, select, textarea, [tabindex="0"]';

export function startProgressPopup(win = window, doc = document) {
    win[runtimeKey]?.dispose();
    const lifetime = new AbortController(), signal = lifetime.signal;
    const availability = new Map(), links = [];
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
        element.title = 'AniDōki';
        const content = node('div', 'detailButton-content'), icon = node('span', 'detailButton-icon doki-item-button-icon');
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

    function open(itemId, { opener = doc.activeElement, fromItemPage = false, push = true } = {}) {
        if (!itemId || disposed || !ready)
            return;
        if (popup && !popup.closing && popup.itemId === itemId)
            return;
        if (popup)
            finish(popup, false);
        const current = { itemId, opener, fromItemPage, controller: new AbortController(), view: null, rows: [], rowKey: null, drafts: {}, openProvider: null,
            pushed: false, closing: false, removed: false, timer: null, menuOpen: false, openOnMenu: false, motion: null, busy: false, store: null,
            cards: {}, notes: {}, timers: new Set() };
        popup = current;
        if (push && requested() !== itemId) {
            const url = new URL(win.location.href);
            url.searchParams.set(parameter, itemId);
            win.history.pushState({ anidoki: itemId }, '', url.pathname + url.search + url.hash);
            current.pushed = true;
        }
        build(current);
        load(current);
    }

    /** Closes the popup. From the close button or Esc, it steps back over its own history entry, or drops a deep link's parameter. */
    function close({ fromHistory = false, replace = false } = {}) {
        const current = popup;
        if (!current || current.closing)
            return;
        current.closing = true;
        current.controller.abort();
        if (!fromHistory && requested()) {
            if (current.pushed && !replace)
                win.history.back();
            else
                win.history.replaceState(win.history.state ?? null, '', withoutParameter());
        }
        current.scrim.classList.add('is-closing');
        current.timer = win.setTimeout(() => finish(current, true), closeFallbackMs);
    }

    function finish(current, restoreFocus) {
        if (current.removed)
            return;
        current.removed = true;
        current.controller.abort();
        win.clearTimeout(current.timer);
        for (const id of current.timers)
            win.clearTimeout(id);
        current.timers.clear();
        current.scrim.remove();
        doc.documentElement.classList?.remove('doki-popup-open');
        if (popup === current)
            popup = null;
        if (restoreFocus) {
            const target = current.opener?.isConnected ? current.opener : doc.body;
            target?.focus?.({ preventScroll: true });
        }
    }

    function trapFocus(current, event) {
        const items = current.panel.querySelectorAll(focusable).filter(item => !item.hidden && !item.closest('[hidden]') && item.getAttribute('tabindex') !== '-1');
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

    function settingsHref() {
        const entry = doc.querySelector('.pluginMenuOptions a[href], [data-plugin-pages] a[href]');
        return entry?.getAttribute('href') ?? api().getUrl('AniDoki/settings');
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

    function build(current) {
        const scrim = node('div', 'anidoki doki-popup-scrim');
        const panel = node('section', 'doki-popup');
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'true');
        panel.setAttribute('aria-labelledby', 'dokiPopupTitle');

        const header = node('header', 'doki-popup-header'), start = node('div', 'doki-popup-header-start');
        const symbol = node('span', 'doki-symbol');
        symbol.setAttribute('aria-hidden', 'true');
        const crumbs = node('nav', 'doki-popup-crumbs');
        crumbs.setAttribute('aria-label', 'Breadcrumb');
        const crumb = node('span', 'doki-popup-crumb', '');
        crumb.setAttribute('aria-current', 'page');
        crumbs.append(crumb);
        const unsaved = node('p', 'doki-popup-unsaved', 'Unsaved changes');
        unsaved.hidden = true;
        const titleRow = node('div', 'doki-popup-crumb-row');
        titleRow.append(symbol, crumbs);
        start.append(titleRow, unsaved);
        const actions = node('div', 'doki-popup-actions');
        const openOn = node('div', 'doki-popup-open-on');
        const settings = chip('a', 'doki-popup-settings', 'AniDōki settings', 'gear');
        settings.setAttribute('href', settingsHref());
        settings.addEventListener('click', () => close({ replace: true }), { signal });
        const closeButton = chip('button', 'doki-popup-close', 'Close', 'close');
        closeButton.addEventListener('click', () => close(), { signal });
        actions.append(openOn, settings, closeButton);
        header.append(start, actions);

        const body = node('div', 'doki-popup-body');
        const hero = node('div', 'doki-popup-hero');
        const layout = node('div', 'doki-popup-layout');
        const side = node('aside', 'doki-popup-side');
        const poster = node('div', 'doki-popup-poster');
        const details = node('dl', 'doki-popup-details');
        details.setAttribute('aria-label', 'Series details');
        details.hidden = true;
        const seasons = node('div', 'doki-popup-seasons');
        seasons.hidden = true;
        side.append(poster, details, seasons);
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
        overviewTab.setAttribute('role', 'tab');
        overviewTab.setAttribute('aria-selected', 'true');
        overviewTab.setAttribute('aria-controls', 'dokiPopupOverview');
        tabs.append(overviewTab);
        const overview = node('div', 'doki-popup-panel');
        overview.id = 'dokiPopupOverview';
        overview.setAttribute('role', 'tabpanel');
        overview.dataset.region = 'overview';
        const status = node('div', 'doki-popup-status');
        status.setAttribute('role', 'status');
        main.append(titleBlock, meta, tabs, status, overview);
        layout.append(side, main);
        body.append(hero, layout);
        panel.append(header, body);
        scrim.append(panel);

        panel.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                event.preventDefault();
                if (current.menuOpen)
                    closeSeasonMenu(current, true);
                else
                    close();
            } else if (event.key === 'Tab')
                trapFocus(current, event);
        }, { signal });
        panel.addEventListener('animationend', event => {
            if (event.target === panel && current.closing)
                finish(current, true);
        }, { signal });

        Object.assign(current, { scrim, panel, crumb, unsaved, openOn, hero, poster, details, seasons, logo, title, meta, seasonMenu, tabs, overview, status });
        doc.documentElement.classList?.add('doki-popup-open');
        doc.body.append(scrim);
        title.focus({ preventScroll: true });
    }

    async function load(current) {
        current.status.replaceChildren(node('p', 'doki-popup-loading', 'Loading…'));
        current.status.hidden = false;
        try {
            const result = await request(`AniDoki/user/progress/series/${encodeURIComponent(current.itemId)}`, { abort: current.controller.signal });
            if (current !== popup || current.closing)
                return;
            if (!result.ok || !result.data)
                throw result;
            receive(current, result.data);
        } catch {
            if (current !== popup || current.closing)
                return;
            const retry = button('doki-button doki-popup-retry', 'Try again');
            retry.addEventListener('click', () => load(current), { signal });
            current.status.replaceChildren(node('p', 'doki-popup-error', "Couldn't load this series"), retry);
        }
    }

    function storage() {
        try {
            return win.sessionStorage ?? null;
        } catch {
            return null;
        }
    }

    function receive(current, view) {
        current.view = view;
        if (!current.store) {
            // D12: pending edits survive closing the popup and reloading the tab, per user and series.
            current.store = draftStore(storage(), api().getCurrentUserId?.() ?? '', view.id);
            current.drafts = current.store.load();
        }
        current.rows = buildRows(view);
        if (!current.rows.some(row => row.key === current.rowKey))
            current.rowKey = defaultRow(view, current.rows)?.key ?? null;
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
        current.crumb.textContent = view.name;
        current.unsaved.hidden = !anyDirty(current.drafts);
        current.title.textContent = view.name;
        const images = new Set(view.images ?? []);
        if (images.has('Backdrop'))
            current.hero.style.backgroundImage = `url("${imageUrl(view.id, 'Backdrop', { maxWidth: 1600 })}")`;
        current.hero.classList.toggle('has-backdrop', images.has('Backdrop'));
        current.poster.replaceChildren();
        if (images.has('Primary')) {
            const img = node('img', '');
            img.alt = '';
            img.src = imageUrl(view.id, 'Primary', { maxHeight: 552 });
            current.poster.append(img);
        }
        current.logo.hidden = !images.has('Logo');
        if (images.has('Logo'))
            current.logo.src = imageUrl(view.id, 'Logo', { maxWidth: 720 });
        current.title.classList.toggle('doki-sr-only', images.has('Logo'));
        renderMeta(current);
        renderDetails(current);
        renderSeasons(current);
        renderHeaderLinks(current);
        renderOverview(current);
    }

    function renderMeta(current) {
        const view = current.view, items = [];
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
        current.details.replaceChildren(...facts.flatMap(([name, value]) => [node('dt', '', name), node('dd', '', value)]));
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
            const options = list.querySelectorAll('[role=option]'), index = options.indexOf(doc.activeElement);
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
        const links = [];
        if (!fromSeriesPage) {
            const jellyfin = chip('a', 'doki-popup-open-jellyfin', 'Open in Jellyfin', 'jellyfin');
            jellyfin.setAttribute('href', `#/details?id=${encodeURIComponent(view.id)}`);
            jellyfin.addEventListener('click', () => close({ replace: true }), { signal });
            links.push(jellyfin);
        }
        current.openOn.replaceChildren(...links, ...trackerLinks(current));
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
        const toggle = chip('button', 'doki-popup-open-tracker', 'Open on tracker', 'external');
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
        current.overview.append(renderPills(current, row));
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
            return { key, name: 'All trackers', targets: editable, editable: true, status: summary.status, statusKey: model.fields.status.value ?? 'none',
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
        const section = node('section', 'doki-pill-open');
        section.dataset.provider = info.key;
        const head = button('doki-pill doki-pill-head');
        head.dataset.focusKey = `pill:${info.key}`;
        head.setAttribute('aria-expanded', 'true');
        head.append(...pillContent(info));
        const chevron = node('span', 'doki-pill-chevron');
        chevron.setAttribute('aria-hidden', 'true');
        head.append(chevron);
        head.addEventListener('click', () => {
            current.openProvider = null;
            current.motion = 'closing';
            refresh(current, `pill:${info.key}`);
        }, { signal });
        section.append(head);
        const note = model.readOnlyReason || model.mixedNote;
        if (note)
            section.append(node('p', 'doki-pill-note', note));
        // Safari does not focus a clicked button, so each control names the focus key to restore after the re-render.
        const edit = (change, focusKey) => {
            setRowDrafts(current, row, change(rowDrafts(current, row)));
            delete current.notes[row.key];
            refresh(current, focusKey);
        };
        section.append(fieldsRow(info, model, edit), progressRow(info, model, edit), ...cardsFor(current, row, info), footer(current, row, info, model, edit));
        return section;
    }

    function field(label, ...controls) {
        const wrap = node('div', 'doki-field');
        wrap.append(node('span', 'doki-field-label', label), ...controls);
        return wrap;
    }

    function fieldsRow(info, model, edit) {
        const row = node('div', 'doki-editor-row doki-editor-fields'), disabled = model.readOnly;
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
        range.disabled = model.readOnly;
        range.setAttribute('aria-label', 'Episodes watched');
        range.style.setProperty('--doki-fill', `${percent(progress.value, progress.max)}%`);
        range.addEventListener('input', () => range.style.setProperty('--doki-fill', `${percent(Number(range.value), progress.max)}%`), { signal });
        range.addEventListener('change', () => edit(drafts => setProgress(info.targets, drafts, Number(range.value)), 'bar'), { signal });
        const tick = node('span', 'doki-track-tick');
        tick.setAttribute('aria-hidden', 'true');
        tick.style.left = `${percent(model.jellyfin, progress.max)}%`;
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
            const tip = node('span', 'doki-tooltip', reason);
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
        if (model.suggestComplete && !model.readOnly) {
            const complete = button('doki-button doki-mark-complete', 'Mark Completed');
            complete.dataset.focusKey = 'complete';
            complete.addEventListener('click', () => edit(drafts => markCompleted(info.targets, drafts), 'status'), { signal });
            notes.append(complete);
        }
        const status = node('p', 'doki-editor-status', current.notes[row.key] ?? (current.busy ? 'Syncing…' : ''));
        status.setAttribute('role', 'status');
        notes.append(status);
        const buttons = syncButtons(info.targets, rowDrafts(current, row), { all: info.key === 'all' });
        const actions = node('div', 'doki-sync-actions');
        actions.append(syncButton(current, 'doki-sync-up', buttons.up.label, buttons.up, 'up', () => syncUp(current, row, info.targets)),
            syncButton(current, 'doki-sync-down', buttons.down.label, buttons.down, 'down', () => syncDown(current, row, info.targets)));
        wrap.append(notes, actions);
        return wrap;
    }

    /** Re-renders the parts an edit changes, keeping focus on the same control. */
    function refresh(current, focusKey = doc.activeElement?.dataset?.focusKey) {
        if (current !== popup || current.removed || !current.view)
            return;
        current.unsaved.hidden = !anyDirty(current.drafts);
        renderDetails(current);
        renderSeasons(current);
        renderHeaderLinks(current);
        renderOverview(current);
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
        delete current.notes[row.key];
        current.cards[row.key] = (current.cards[row.key] ?? []).filter(card => !byProvider[card.provider]);
        refresh(current);
        let results = [], note = null;
        try {
            if (plan.edit) {
                const started = await request('AniDoki/user/progress/edit', { method: 'POST', body: plan.edit, abort: current.controller.signal });
                if (!started.ok || !started.data?.operationId)
                    throw started;
                const outcome = await poll(current, `AniDoki/user/progress/edit/${started.data.operationId}`, data => data.finished);
                if (outcome)
                    results = outcome.results;
                else
                    note = 'Still saving. Check History in AniDōki settings';
            }
            for (const provider of note ? [] : plan.syncs) {
                if (!await runSync(current, 'sync-to-trackers', provider)) {
                    note = 'Still syncing. Check History in AniDōki settings';
                    break;
                }
            }
        } catch (failure) {
            note = failureNote(failure);
        }
        await settle(current, row, results, byProvider, note);
    }

    async function syncDown(current, row, targets) {
        const provider = planSyncDown(targets, rowDrafts(current, row));
        if (current.busy || !provider)
            return;
        current.busy = true;
        delete current.notes[row.key];
        refresh(current);
        let note = null;
        try {
            if (!await runSync(current, 'sync-to-jellyfin', provider))
                note = 'Still syncing. Check History in AniDōki settings';
        } catch (failure) {
            note = failureNote(failure);
        }
        await settle(current, row, [], {}, note);
    }

    /** After a save or sync: read the view again, apply the edit results to the drafts, and show what needs the user. */
    async function settle(current, row, results, byProvider, note) {
        if (current !== popup || current.removed)
            return;
        current.busy = false;
        const applied = applyEditResults(rowDrafts(current, row), results, byProvider);
        setRowDrafts(current, row, applied.drafts);
        current.cards[row.key] = [...(current.cards[row.key] ?? []), ...applied.cards];
        if (note)
            current.notes[row.key] = note;
        try {
            const fresh = await request(`AniDoki/user/progress/series/${encodeURIComponent(current.itemId)}`, { abort: current.controller.signal });
            if (fresh.ok && fresh.data && current === popup && !current.removed) {
                current.view = fresh.data;
                current.rows = buildRows(fresh.data);
            }
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
        scanPage();
    }

    function dispose() {
        if (disposed)
            return;
        disposed = true;
        lifetime.abort();
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

    doc.addEventListener('viewshow', () => scanPage(), { signal });
    win.addEventListener('hashchange', () => scanPage(), { signal });
    win.addEventListener('popstate', () => {
        const wanted = requested();
        if (popup && !popup.closing && wanted !== popup.itemId)
            close({ fromHistory: true });
        if ((!popup || popup.closing) && wanted)
            open(wanted, { push: false });
    }, { signal });

    win[runtimeKey] = { open, close, dispose };
    bootstrap();
    return win[runtimeKey];
}
