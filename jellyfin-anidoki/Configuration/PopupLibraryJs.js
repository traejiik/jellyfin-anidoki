import { providerName } from './common.js';
import { libraryCard, libraryResults, libraryStatuses, libraryStatusLabel } from './popup-library-state.js';

const types = { all: 'All', series: 'Series', movie: 'Movies' };

export function createPopupLibrary({ root, state, title, node, button, chip, imageUrl, providerMark, openSeries, selectProvider, refresh, signal }) {
    const toolbar = node('div', 'doki-library-toolbar');
    const type = node('div', 'doki-library-types');
    type.setAttribute('role', 'group');
    type.setAttribute('aria-label', 'Type');
    const segments = Object.entries(types).map(([key, label]) => {
        const segment = button('doki-library-segment', label);
        segment.addEventListener('click', () => {
            state.type = key;
            renderFilters();
            renderResults(true);
        }, { signal });
        type.append(segment);
        return [key, segment];
    });
    const tracker = menu('Tracker', key => selectProvider(key));
    const status = menu('Status', key => {
        state.status = key;
        renderFilters();
        renderResults(true);
    });
    const refreshButton = chip('button', 'doki-library-refresh', 'Refresh tracker list', 'library-refresh');
    const searchButton = chip('button', 'doki-library-search-toggle', 'Search', 'search');
    const layoutButton = chip('button', 'doki-library-layout-toggle', 'Show as list', 'posters');
    const search = node('div', 'doki-library-search');
    const searchIcon = node('span', 'doki-popup-chip-icon');
    searchIcon.dataset.icon = 'search';
    searchIcon.setAttribute('aria-hidden', 'true');
    const input = node('input', '');
    input.type = 'search';
    input.placeholder = 'Search your anime';
    input.setAttribute('aria-label', 'Search your anime');
    search.append(searchIcon, input);
    const notice = node('div', 'doki-library-notice');
    notice.setAttribute('role', 'status');
    const scroll = node('div', 'doki-library-scroll');
    const results = node('ul', 'doki-library-results');
    results.setAttribute('role', 'list');
    scroll.append(results);
    toolbar.append(type, tracker.root, status.root, refreshButton, node('span', 'doki-library-spacer'), searchButton, layoutButton);
    root.append(toolbar, search, notice, scroll);

    let opened = null;
    function closeMenu(focus = false) {
        if (!opened)
            return false;
        const current = opened;
        opened = null;
        current.list.hidden = true;
        current.trigger.setAttribute('aria-expanded', 'false');
        if (focus)
            current.trigger.focus();
        return true;
    }

    function menu(label, select) {
        const group = node('div', 'doki-library-menu');
        const trigger = button('doki-library-menu-button', label, label);
        trigger.setAttribute('aria-haspopup', 'menu');
        trigger.setAttribute('aria-expanded', 'false');
        const list = node('div', 'doki-library-menu-options');
        list.hidden = true;
        list.setAttribute('role', 'menu');
        list.setAttribute('aria-label', label);
        const current = { root: group, trigger, list };
        const options = () => [...list.querySelectorAll('[role="menuitemradio"]')];
        function open(last = false) {
            closeMenu();
            opened = current;
            list.hidden = false;
            trigger.setAttribute('aria-expanded', 'true');
            list.style.setProperty('--doki-library-menu-shift', '0px');
            const boundary = root.getBoundingClientRect();
            const bounds = list.getBoundingClientRect();
            const margin = 8;
            const shift = Math.max(0, boundary.left + margin - bounds.left) - Math.max(0, bounds.right - boundary.right + margin);
            list.style.setProperty('--doki-library-menu-shift', shift + 'px');
            const items = options();
            (last ? items.at(-1) : items.find(item => item.getAttribute('aria-checked') === 'true') ?? items[0])?.focus();
        }
        trigger.addEventListener('click', () => opened === current ? closeMenu() : open(), { signal });
        trigger.addEventListener('keydown', event => {
            if (!['ArrowDown', 'ArrowUp'].includes(event.key))
                return;
            event.preventDefault();
            open(event.key === 'ArrowUp');
        }, { signal });
        list.addEventListener('keydown', event => {
            const items = options();
            const index = items.indexOf(event.target);
            const next = { Home: 0, End: items.length - 1, ArrowDown: (index + 1) % items.length,
                ArrowUp: (index + items.length - 1) % items.length }[event.key];
            if (next !== undefined) {
                event.preventDefault();
                items[next]?.focus();
            }
        }, { signal });
        current.render = (caption, entries, chosen) => {
            const chevron = node('span', 'doki-library-chevron doki-popup-chip-icon');
            chevron.dataset.icon = 'dropdown';
            chevron.setAttribute('aria-hidden', 'true');
            trigger.replaceChildren(...caption, chevron);
            list.replaceChildren(...entries.map(({ key, mark, text, count, reason }) => {
                const option = button('doki-library-option');
                option.setAttribute('role', 'menuitemradio');
                option.setAttribute('aria-checked', String(key === chosen));
                option.setAttribute('tabindex', '-1');
                if (reason)
                    option.setAttribute('aria-disabled', 'true');
                const labelText = node('span', 'doki-library-option-label', text);
                if (reason)
                    labelText.append(node('small', '', reason));
                option.append(mark, labelText);
                if (count != null)
                    option.append(node('span', 'doki-library-option-count', String(count)));
                option.addEventListener('click', () => {
                    if (!reason) {
                        closeMenu(true);
                        select(key);
                    }
                }, { signal });
                return option;
            }));
            trigger.disabled = !entries.length;
        };
        group.append(trigger, list);
        return current;
    }

    const dot = key => {
        const mark = node('span', 'doki-library-dot');
        mark.dataset.status = key;
        mark.setAttribute('aria-hidden', 'true');
        return mark;
    };
    const filtered = () => libraryResults(state.view, state);
    function renderFilters() {
        for (const [key, segment] of segments)
            segment.setAttribute('aria-pressed', String(state.type === key));
        const provider = state.provider;
        const trackerEntries = (state.view?.providers ?? []).map(({ provider: key, available }) => ({
            key, text: providerName(key), mark: providerMark(key),
            reason: available ? null : 'Library view is not available for ' + providerName(key) + ' yet'
        }));
        tracker.render(provider ? [providerMark(provider), node('span', '', providerName(provider))] : [node('span', '', 'Tracker')], trackerEntries, provider);
        const { counts } = filtered();
        status.render([dot(state.status), node('span', '', libraryStatusLabel(state.status))],
            state.view?.selectedProvider ? libraryStatuses.map(key => ({ key, mark: dot(key), text: libraryStatusLabel(key), count: counts[key] })) : [], state.status);
        refreshButton.disabled = !provider || state.busy;
        refreshButton.setAttribute('aria-label', provider ? 'Refresh from ' + providerName(provider) : 'Refresh tracker list');
        refreshButton.title = refreshButton.getAttribute('aria-label');
        refreshButton.classList.toggle('is-refreshing', state.busy && state.refreshing);
        searchButton.setAttribute('aria-expanded', String(state.searchOpen));
        search.hidden = !state.searchOpen;
        input.value = state.query;
        layoutButton.setAttribute('aria-label', state.layout === 'posters' ? 'Show as list' : 'Show as posters');
        layoutButton.title = layoutButton.getAttribute('aria-label');
        layoutButton.querySelector('.doki-popup-chip-icon').dataset.icon = state.layout;
    }

    function card(row, index) {
        const item = node('li', '');
        const model = libraryCard(row);
        const action = button('doki-library-card');
        const unavailable = row.matchState === 'Unresolved';
        action.classList.toggle('is-unavailable', unavailable);
        action.dataset.itemId = row.itemId;
        action.style.setProperty('--doki-library-delay', Math.min(index, 7) * 40 + 'ms');
        const libraryCount = row.episodeCount + (row.isMovie ? ' movie in your Jellyfin library' : ' episodes in your Jellyfin library for this match');
        const unlogged = model.unlogged == null ? null : model.unlogged + ' not yet logged';
        const airing = model.countdown && 'Next episode ' + (model.countdown === 'Within an hour' ? 'within an hour' : 'in ' + model.countdown);
        action.setAttribute('aria-label', [row.title, model.status, model.progress, libraryCount, unlogged,
            model.score && 'Rating ' + model.score + ' out of 10', airing].filter(Boolean).join(', '));
        const artwork = node('span', 'doki-library-art');
        if (row.hasPoster) {
            const image = node('img', '');
            image.alt = '';
            image.loading = 'lazy';
            image.src = imageUrl(row.itemId, 'Primary', { maxHeight: state.layout === 'posters' ? 430 : 192 });
            image.addEventListener('error', () => image.remove(), { once: true, signal });
            artwork.append(image);
        }
        const metadata = node('span', 'doki-library-card-meta');
        metadata.append(dot(row.status), node('span', '', unavailable ? 'Unavailable' : model.status));
        if (row.year)
            metadata.append(node('span', 'doki-library-card-year', '· ' + row.year));
        const text = node('span', 'doki-library-card-text');
        text.append(metadata, node('strong', 'doki-library-card-title', row.title));
        if (model.solid !== null) {
            const bar = node('span', 'doki-library-bar');
            bar.setAttribute('aria-hidden', 'true');
            bar.style.setProperty('--doki-library-solid', model.solid * 100 + '%');
            bar.style.setProperty('--doki-library-tint', model.tint * 100 + '%');
            bar.append(node('span', 'doki-library-bar-solid'), node('span', 'doki-library-bar-tint'));
            text.append(bar);
        }
        const stats = node('span', 'doki-library-card-stats');
        const statsLine = node('span', 'doki-library-stats-line');
        if (model.score) {
            const score = node('span', 'doki-library-score');
            const star = node('span', 'doki-popup-chip-icon');
            star.dataset.icon = 'library-star';
            star.setAttribute('aria-hidden', 'true');
            score.append(star, node('span', '', model.score));
            statsLine.append(score);
        }
        if (model.progress !== null) {
            const progress = node('span', 'doki-library-progress');
            const [count, total] = model.progress.split('/');
            const ratio = node('span', 'doki-library-ratio');
            ratio.append(node('strong', 'doki-library-watched', count));
            if (total)
                ratio.append(node('span', 'doki-library-total', '/' + total));
            const badge = node('span', 'doki-library-badge', String(row.episodeCount));
            badge.classList.toggle('has-unlogged', model.unlogged > 0);
            badge.title = [libraryCount, unlogged].filter(Boolean).join(', ');
            progress.append(ratio, badge);
            statsLine.append(progress);
        } else if (!unavailable) {
            statsLine.append(node('span', 'doki-library-progress doki-library-untracked', model.status));
        }
        stats.append(statsLine);
        if (model.countdown)
            stats.append(node('span', 'doki-library-airing', model.countdown));
        action.append(artwork, text, stats);
        if (unavailable) {
            const tooltip = node('span', 'doki-library-match-tooltip', model.status);
            tooltip.setAttribute('aria-hidden', 'true');
            action.append(tooltip);
        }
        action.addEventListener('click', () => {
            state.scrollTop = scroll.scrollTop || 0;
            state.focusId = row.itemId;
            openSeries(row.itemId);
        }, { signal });
        item.append(action);
        return item;
    }

    function renderResults(resetScroll = false) {
        if (resetScroll)
            scroll.scrollTop = 0;
        results.classList.toggle('is-list', state.layout === 'list');
        const { rows, counts } = filtered();
        results.replaceChildren(...rows.map(card));
        scroll.setAttribute('aria-busy', String(state.busy));
        notice.replaceChildren();
        if (state.error) {
            notice.append(node('p', 'doki-popup-error', state.error));
            const retry = button('doki-button', 'Try again');
            retry.addEventListener('click', refresh, { signal });
            notice.append(retry);
        } else if (state.busy && !rows.length) {
            notice.append(node('span', 'doki-sr-only', 'Loading your anime…'));
            for (let i = 0; i < 5; i++) {
                const placeholder = node('li', 'doki-library-skeleton');
                placeholder.setAttribute('aria-hidden', 'true');
                results.append(placeholder);
            }
        } else if (state.view && !state.view.selectedProvider) {
            notice.append(node('strong', '', 'No supported trackers linked'),
                node('p', '', 'Link MyAnimeList, AniList, or Kitsu to see your anime here.'));
        } else if (!rows.length && state.view) {
            const noMovies = state.type === 'movie' && counts.all === 0;
            notice.append(node('strong', '', noMovies ? 'No anime movies in your libraries' : 'Nothing matches'),
                node('p', '', noMovies ? 'Movies in your anime libraries will show up here.' : 'Try another status or clear the search.'));
        }
        notice.hidden = !notice.children.length;
    }

    input.addEventListener('input', () => {
        state.query = input.value;
        renderResults(true);
    }, { signal });
    searchButton.addEventListener('click', () => {
        state.searchOpen = !state.searchOpen;
        if (!state.searchOpen)
            state.query = '';
        renderFilters();
        renderResults(true);
        if (state.searchOpen)
            input.focus();
    }, { signal });
    layoutButton.addEventListener('click', () => {
        state.layout = state.layout === 'posters' ? 'list' : 'posters';
        renderFilters();
        renderResults(true);
    }, { signal });
    refreshButton.addEventListener('click', refresh, { signal });
    root.addEventListener('click', event => {
        if (opened && !opened.root.contains(event.target))
            closeMenu();
    }, { signal });
    root.addEventListener('focusout', event => {
        if (opened && event.relatedTarget && !opened.root.contains(event.relatedTarget))
            closeMenu();
    }, { signal });

    return {
        closeMenu,
        render() {
            closeMenu();
            renderFilters();
            renderResults();
        },
        restoreFocus() {
            if (!root.isConnected)
                return;
            scroll.scrollTop = state.scrollTop || 0;
            const target = [...results.querySelectorAll('.doki-library-card')].find(card => card.dataset.itemId === state.focusId);
            (target ?? title).focus({ preventScroll: true });
        }
    };
}
