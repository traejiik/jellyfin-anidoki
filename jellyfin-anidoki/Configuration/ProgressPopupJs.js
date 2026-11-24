import { buildRows, defaultRow, rowTargets, editableTargets, pillSummary, allSummary, airedLabel, seriesStatusLabel } from './popup-state.js';

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
        if (disposed || !ready || !itemId || !api()?.isLoggedIn?.())
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
            pushed: false, closing: false, removed: false, timer: null, menuOpen: false };
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

    function receive(current, view) {
        current.view = view;
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

    // Filled in by the Overview: the Open on <tracker> chip and menu.
    function trackerLinks() {
        return [];
    }

    function renderOverview(current) {
        const view = current.view;
        current.overview.replaceChildren();
        if (view.overview)
            current.overview.append(node('p', 'doki-popup-description', view.overview));
        // D10: below 900px the Season menu sits after the description and before the pills; CSS hides it on wider screens.
        current.overview.append(current.seasonMenu);
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
