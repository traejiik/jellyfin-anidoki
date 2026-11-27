import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';
import { createDocument, createWindow, flush, keydown } from './fake-dom.mjs';

const { startProgressPopup } = await importConfiguration('ProgressPopupJs.js');

const seriesId = 'beb17bb44002f5b7aa1e11d1a6cbc0a8';
const seriesView = (extra = {}) => ({
    id: seriesId, name: 'Cowboy Bebop', isMovie: false, year: 1998, endYear: 1999, communityRating: 8.4, officialRating: 'TV-14', status: 'Ended',
    overview: 'Space bounty hunters.', premiereDate: '1998-04-03T00:00:00Z', images: ['Primary', 'Backdrop'], episodeCount: 26, defaultSeason: 'bebop:1',
    matchesLinkAvailable: true, trackers: [{ provider: 'AniList', supportsScore: true, scoreFormat: 'Point10Decimal' }],
    seasons: [{ sourceKey: 'bebop:1', season: 1, episodes: [1, 2, 3], watched: [1, 2, 3], lastPlayedUtc: null, trackers: [{ provider: 'AniList', origin: 'Automatic',
        unresolved: null, error: null, entries: [{ id: 1, alternativeId: null, title: 'Cowboy Bebop', totalEpisodes: 26, firstEpisode: 1, lastEpisode: 26,
            status: 'Watching', progress: 3, score: null, rewatching: false, jellyfinProgress: 3 }] }] }],
    ...extra
});

/** A Jellyfin web page with one item detail page, and a server answering the popup's requests through routes. */
function setup({ href = `http://jf/web/index.html#/details?id=${seriesId}`, available = 204, routes = {} } = {}) {
    const doc = createDocument(), win = createWindow(href);
    const page = doc.createElement('div');
    page.className = 'page libraryPage itemDetailPage';
    const row = doc.createElement('div');
    row.className = 'mainDetailButtons focuscontainer-x';
    const favourite = doc.createElement('button');
    favourite.className = 'button-flat btnUserRating detailButton emby-button';
    const more = doc.createElement('button');
    more.className = 'button-flat btnMoreCommands detailButton emby-button';
    row.append(favourite, more);
    page.append(row);
    doc.body.append(page);
    const requests = [];
    win.fetch = async (url, init = {}) => {
        requests.push({ url, method: init.method ?? 'GET', body: init.body ? JSON.parse(init.body) : null });
        const route = Object.entries(routes).find(([pattern]) => url.includes(pattern) && (!url.endsWith('/summary') || pattern.endsWith('/summary')));
        if (url.includes('/available'))
            return { ok: available < 300, status: available, json: async () => null };
        if (route) {
            const answer = await route[1]({ url, init, requests });
            return { ok: (answer.status ?? 200) < 300, status: answer.status ?? 200, json: async () => answer.body };
        }
        if (url.includes('AniDoki/user/progress/series/'))
            return { ok: true, status: 200, json: async () => seriesView() };
        return { ok: false, status: 404, json: async () => null };
    };
    win.sessionStorage = { getItem: () => null, setItem() {}, removeItem() {} };
    win.ApiClient = {
        isLoggedIn: () => true, getCurrentUserId: () => 'user', setRequestHeaders: headers => { headers.Authorization = 'fixture'; },
        getUrl: (path, params) => `/jf/${path}${params && Object.keys(params).length ? `?${new URLSearchParams(params)}` : ''}`
    };
    return { doc, win, page, row, favourite, more, requests };
}

const dialog = doc => doc.querySelector('[role=dialog]');

function pluginPagesLink(doc, href = '#/userpluginsettings.html?pageUrl=%2Fjf%2FAniDoki%2Fsettings') {
    const menu = doc.createElement('div');
    menu.className = 'pluginMenuOptions';
    const link = doc.createElement('a');
    const label = doc.createElement('span');
    link.setAttribute('href', href);
    link.append(label);
    menu.append(link);
    doc.body.append(menu);
    return { menu, link, label };
}

function activateMenuLink(doc, target, options = {}) {
    const event = new Event('click', { cancelable: true });
    Object.defineProperty(event, 'target', { value: target });
    Object.assign(event, options);
    doc.dispatchEvent(event);
    return event.defaultPrevented;
}

test('the Plugin Pages entry opens the library and still works after a menu rebuild', async () => {
    const env = setup({ href: 'http://jf/jf/web/#/home', routes: { '/library': () => ({ body: libraryView() }) } });
    const { menu, label } = pluginPagesLink(env.doc);
    assert.equal(activateMenuLink(env.doc, label), false);
    const runtime = startProgressPopup(env.win, env.doc);
    assert.equal(activateMenuLink(env.doc, label), true);
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Your anime');
    assert.equal(env.win.location.hash, '#/home');
    assert.equal(env.win.history.length, 2);
    dialog(env.doc).querySelector('[aria-label=Close]').click();
    await env.win.advance(250);
    menu.remove();
    const rebuilt = pluginPagesLink(env.doc, '#/userpluginsettings.html?pageUrl=/AniDoki/settings');
    assert.equal(activateMenuLink(env.doc, rebuilt.label), true);
    await flush();
    assert.ok(dialog(env.doc).querySelector('.doki-library-card'));
    runtime.dispose();
    assert.equal(activateMenuLink(env.doc, rebuilt.label), false);
});

test('the Plugin Pages entry uses the item popup only for eligible details', async () => {
    for (const available of [204, 404]) {
        const env = setup({ available, routes: { '/library': () => ({ body: libraryView() }) } });
        const { label } = pluginPagesLink(env.doc);
        const runtime = startProgressPopup(env.win, env.doc);
        assert.equal(activateMenuLink(env.doc, label), true);
        await flush();
        assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, available === 204 ? 'Cowboy Bebop' : 'Your anime');
        assert.equal(Boolean(dialog(env.doc).querySelector('.doki-popup-open-jellyfin')), false);
        assert.equal(env.requests.filter(request => request.url.endsWith('/available')).length, 1);
        runtime.dispose();
    }
});

test('the account menu closes before the popup opens and gets focus back from Close', async () => {
    const env = setup({ href: 'http://jf/web/#/home', routes: { '/library': () => ({ body: libraryView() }) } });
    const { menu, link, label } = pluginPagesLink(env.doc);
    menu.id = 'app-user-menu';
    const trigger = env.doc.createElement('button');
    trigger.setAttribute('aria-controls', menu.id);
    env.doc.body.append(trigger);
    env.win.KeyboardEvent = class extends Event {
        constructor(type, options) {
            super(type, options);
            this.key = options.key;
        }
    };
    let escaped = false;
    link.addEventListener('keydown', event => {
        escaped = event.key === 'Escape' && event.bubbles;
        menu.remove();
    });
    const runtime = startProgressPopup(env.win, env.doc);
    assert.equal(activateMenuLink(env.doc, label), true);
    assert.equal(escaped, true);
    assert.equal(dialog(env.doc), null);
    await env.win.advance(16);
    dialog(env.doc).querySelector('[aria-label=Close]').click();
    await env.win.advance(250);
    assert.equal(env.doc.activeElement, trigger);
    runtime.dispose();
});

test('the Plugin Pages takeover leaves other settings links and modified activations alone', async () => {
    const env = setup({ href: 'http://jf/web/#/home' });
    const { label, link } = pluginPagesLink(env.doc);
    const { label: otherPlugin } = pluginPagesLink(env.doc, '#/userpluginsettings.html?pageUrl=/Other/settings');
    const { label: wrongRoute } = pluginPagesLink(env.doc, '#/home?pageUrl=/AniDoki/settings');
    const { label: wrongOrigin } = pluginPagesLink(env.doc, 'http://other/web/#/userpluginsettings.html?pageUrl=/AniDoki/settings');
    const ordinary = env.doc.createElement('a');
    ordinary.setAttribute('href', link.getAttribute('href'));
    env.doc.body.append(ordinary);
    const runtime = startProgressPopup(env.win, env.doc);
    for (const target of [otherPlugin, wrongRoute, wrongOrigin, ordinary])
        assert.equal(activateMenuLink(env.doc, target), false);
    for (const options of [{ button: 1 }, { ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { altKey: true }])
        assert.equal(activateMenuLink(env.doc, label, options), false);
    link.setAttribute('target', '_blank');
    assert.equal(activateMenuLink(env.doc, label), false);
    link.removeAttribute('target');
    env.win.ApiClient.isLoggedIn = () => false;
    assert.equal(activateMenuLink(env.doc, label), false);
    assert.equal(dialog(env.doc), null);
    runtime.dispose();
});

test('a cold Discord link steps back to the library then Jellyfin details and supports Forward and reload', async () => {
    const details = `http://jf/jf/web/index.html?extra=1#/details?id=${seriesId}&serverId=server&context=tvshows`;
    const env = setup({ href: `${details}&anidoki=${seriesId}`, routes: { '/library': () => ({ body: libraryView() }) } });
    const runtime = startProgressPopup(env.win, env.doc);
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    assert.equal(env.win.history.length, 3);
    assert.equal(new URL(env.win.location.href).searchParams.get('anidoki'), seriesId);
    assert.equal(env.win.location.hash, new URL(details).hash);
    assert.equal(new URL(env.win.location.href).searchParams.get('extra'), '1');
    assert.equal(env.requests.filter(request => request.url.includes('/library')).length, 0);
    runtime.dispose();
    const restarted = startProgressPopup(env.win, env.doc);
    await flush();
    env.win.history.back();
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Your anime');
    env.win.history.back();
    await env.win.advance(250);
    assert.equal(env.win.location.href, details);
    assert.equal(dialog(env.doc), null);
    env.win.history.forward();
    env.win.history.forward();
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    keydown(dialog(env.doc), 'Escape');
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Your anime');
    env.win.history.forward();
    await flush();
    dialog(env.doc).querySelector('[aria-label=Close]').click();
    await env.win.advance(250);
    assert.equal(env.win.location.href, details);
    assert.equal(dialog(env.doc), null);
    restarted.dispose();
});

test('a Discord marker can only open its Jellyfin detail item', async () => {
    for (const route of [`#/home?id=${seriesId}&anidoki=${seriesId}`, `#/details?id=other&anidoki=${seriesId}`]) {
        const env = setup({ href: `http://jf/web/${route}` });
        const runtime = startProgressPopup(env.win, env.doc);
        await flush();
        assert.equal(dialog(env.doc), null);
        assert.equal(env.win.history.length, 1);
        runtime.dispose();
    }
});

test('the popup uses the host theme layers without the item backdrop', async () => {
    const env = setup();
    const backdrop = env.doc.createElement('div');
    backdrop.className = 'backgroundContainer withBackdrop';
    env.doc.body.append(backdrop);
    env.win.getComputedStyle = element => ({
        background: element === env.doc.documentElement ? 'rgb(16, 16, 16)' :
            element === backdrop ? 'url(item-art.jpg)' : element.className === 'backgroundContainer' ? 'rgba(17, 14, 30, 0.74)' : 'transparent',
        opacity: '1'
    });

    const popup = await openPopup(env);
    const background = dialog(env.doc).querySelector('.doki-popup-background');
    assert.deepEqual(Array.from(background.children, layer => layer.style.background), ['rgb(16, 16, 16)', 'transparent', 'rgba(17, 14, 30, 0.74)']);
    assert.equal(env.doc.querySelectorAll('.backgroundContainer').length, 1);

    // Jellyfin loads the user's theme after a cold deep link has opened the popup.
    env.win.getComputedStyle = element => ({ background: element === env.doc.documentElement ? 'rgb(242, 242, 242)' : 'transparent', opacity: '1' });
    const theme = env.doc.createElement('link');
    const load = new Event('load');
    Object.defineProperty(load, 'target', { value: theme });
    env.doc.dispatchEvent(load);
    await env.win.advance(16);
    const layers = dialog(env.doc).querySelectorAll('.doki-popup-background');
    assert.equal(layers.length, 1);
    assert.equal(layers[0].children[0].style.background, 'rgb(242, 242, 242)');
    popup.dispose();
});

test('button appears once on an anime item page', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    env.doc.dispatchEvent(new Event('viewshow'));
    env.doc.dispatchEvent(new Event('viewshow'));
    await flush();
    const buttons = env.row.querySelectorAll('.doki-item-button');
    assert.equal(buttons.length, 1);
    assert.equal(env.row.children.indexOf(buttons[0]), env.row.children.indexOf(env.more) - 1);
    assert.equal(buttons[0].getAttribute('aria-label'), 'AniDōki: this series on your trackers');
    assert.equal(env.requests.filter(request => request.url.includes('/available')).length, 1);
    popup.dispose();
});

test('no button when unavailable', async () => {
    const env = setup({ available: 404 });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    assert.equal(env.row.querySelectorAll('.doki-item-button').length, 0);
    popup.dispose();
});

test('open pushes history and back closes', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const button = env.row.querySelector('.doki-item-button');
    button.focus();
    button.click();
    await flush();
    assert.equal(new URL(env.win.location.href).searchParams.get('anidoki'), seriesId);
    const panel = dialog(env.doc);
    assert.equal(panel.getAttribute('aria-modal'), 'true');
    assert.ok(env.doc.activeElement === env.doc.getElementById('dokiPopupTitle'), 'focus moves to the heading');
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    env.win.history.back();
    await env.win.advance(250);
    assert.ok(dialog(env.doc) === null);
    assert.ok(env.doc.activeElement === button, 'focus returns to the opener');
    popup.dispose();
});

test('the close button steps back through history', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    env.row.querySelector('.doki-item-button').click();
    await flush();
    dialog(env.doc).querySelector('[aria-label=Close]').click();
    await env.win.advance(250);
    assert.ok(dialog(env.doc) === null);
    assert.equal(new URL(env.win.location.href).searchParams.get('anidoki'), null);
    popup.dispose();
});

test('a deep link opens on start', async () => {
    const env = setup({ href: `http://jf/web/index.html?anidoki=${seriesId}#/home` });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    assert.ok(dialog(env.doc));
    assert.ok(env.requests.some(request => request.url.includes(`AniDoki/user/progress/series/${seriesId}`) && !request.url.includes('available')));
    assert.ok(dialog(env.doc).querySelector('.doki-popup-open-jellyfin'), 'opened elsewhere, so Open in Jellyfin shows');
    const links = dialog(env.doc).querySelector('.doki-popup-open-on');
    assert.equal(links.children[0].getAttribute('aria-label'), 'Open on AniList');
    assert.equal(links.children[1].getAttribute('aria-label'), 'Open in Jellyfin');
    dialog(env.doc).querySelector('[aria-label=Close]').click();
    await env.win.advance(250);
    assert.equal(env.win.location.href, 'http://jf/web/index.html#/home', 'the deep link is replaced, not stepped back from');
    popup.dispose();
});

test('escape closes', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    popup.open(seriesId, { opener: env.favourite });
    await flush();
    const event = keydown(dialog(env.doc), 'Escape');
    assert.equal(event.defaultPrevented, true);
    await env.win.advance(250);
    assert.ok(dialog(env.doc) === null);
    popup.dispose();
});

test('a series that cannot load offers to try again', async () => {
    let calls = 0;
    const env = setup({ routes: { 'AniDoki/user/progress/series/': () => ++calls === 1 ? { status: 502, body: null } : { body: seriesView() } } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    popup.open(seriesId, {});
    await flush();
    const retry = dialog(env.doc).querySelector('.doki-popup-retry');
    assert.ok(retry);
    assert.equal(dialog(env.doc).querySelector('.doki-overview-skeleton'), null);
    assert.equal(dialog(env.doc).querySelector('.doki-details-skeleton').hidden, true);
    retry.click();
    assert.ok(dialog(env.doc).querySelector('.doki-overview-skeleton'));
    assert.equal(dialog(env.doc).querySelector('.doki-details-skeleton').hidden, false);
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    popup.dispose();
});

test('the series title, artwork and metadata appear while tracker progress is still loading', async () => {
    let finishRead;
    const pending = new Promise(resolve => { finishRead = resolve; });
    const env = setup({ routes: { 'progress/series/': () => pending } });
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    assert.ok(panel.querySelector('.doki-popup-poster img'));
    assert.deepEqual(Array.from(panel.querySelectorAll('.doki-popup-meta-item'), item => item.textContent),
        ['1998–1999', '★ 8.4', 'TV-14', 'Finished']);
    assert.equal(panel.querySelector('.doki-popup-status .doki-sr-only').textContent, 'Loading tracker progress…');
    const skeleton = panel.querySelector('.doki-overview-skeleton');
    assert.equal(skeleton.getAttribute('aria-hidden'), 'true');
    assert.ok(skeleton.querySelector('.doki-skeleton-description'));
    assert.ok(skeleton.querySelector('.doki-tracker-skeleton-pill'));
    const detailsSkeleton = panel.querySelector('.doki-details-skeleton');
    assert.equal(detailsSkeleton.hidden, false);
    assert.equal(detailsSkeleton.getAttribute('aria-hidden'), 'true');
    assert.equal(detailsSkeleton.querySelectorAll('.doki-skeleton-line').length, 8);
    assert.equal(panel.querySelector('.doki-popup-tabs').hidden, false);
    panel.querySelector('#dokiPopupMatchesTab').click();
    assert.equal(panel.querySelector('#dokiPopupMatches').getAttribute('aria-busy'), 'true');
    assert.equal(panel.querySelector('#dokiPopupMatches').hidden, false);
    assert.equal(panel.querySelector('#dokiPopupOverview').hidden, true);
    assert.equal(panel.querySelectorAll('.doki-matches-skeleton .doki-popup-match-group').length, 2);
    finishRead({ body: seriesView() });
    await flush();
    assert.equal(panel.querySelector('.doki-popup-status').hidden, true);
    assert.equal(panel.querySelector('.doki-overview-skeleton'), null);
    assert.equal(panel.querySelector('.doki-matches-skeleton'), null);
    assert.equal(panel.querySelector('#dokiPopupMatches').getAttribute('aria-busy'), null);
    assert.equal(panel.querySelector('#dokiPopupMatches').hidden, false);
    assert.equal(detailsSkeleton.hidden, true);
    assert.equal(panel.querySelector('dl.doki-popup-details').hidden, false);
    assert.ok(pill(env.doc, 'AniList'));
    popup.dispose();
});

test('dispose removes everything', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    popup.open(seriesId, {});
    await flush();
    popup.dispose();
    assert.ok(dialog(env.doc) === null);
    assert.equal(env.row.querySelectorAll('.doki-item-button').length, 0);
    assert.equal(env.doc.head.querySelectorAll('link').length, 0);
    assert.equal(env.win.timers.size, 0);
    const before = env.requests.length;
    env.doc.dispatchEvent(new Event('viewshow'));
    env.win.dispatchEvent(new Event('popstate'));
    await flush();
    assert.equal(env.requests.length, before);
});

// --- Task 6: pills, editor, sync and cards ---
const entryOf = (id, extra = {}) => ({ id, alternativeId: null, title: 'Cowboy Bebop', totalEpisodes: 26, firstEpisode: 1, lastEpisode: 26, status: 'Watching',
    progress: 3, score: null, rewatching: false, jellyfinProgress: 3, ...extra });
const twoTrackers = (aniList = {}, kitsu = {}) => seriesView({
    trackers: [{ provider: 'AniList', supportsScore: true, scoreFormat: 'Point10Decimal' }, { provider: 'Kitsu', supportsScore: true, scoreFormat: 'Point10Half' }],
    seasons: [{ sourceKey: 'bebop:1', season: 1, episodes: [1, 2, 3], watched: [1, 2, 3], lastPlayedUtc: null, trackers: [
        { provider: 'AniList', origin: 'Automatic', unresolved: null, error: null, entries: [entryOf(1, aniList)] },
        { provider: 'Kitsu', origin: 'Automatic', unresolved: null, error: null, entries: [entryOf(9, kitsu)] }] }]
});

async function openPopup(env) {
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    popup.open(seriesId, {});
    await flush();
    return popup;
}
const pill = (doc, key) => doc.querySelector(`[data-focus-key="pill:${key}"]`);
const posts = (env, part) => env.requests.filter(request => request.method === 'POST' && request.url.includes(part));
const gets = (env, part) => env.requests.filter(request => request.method === 'GET' && request.url.includes(part) && !request.url.endsWith('/available') && !request.url.endsWith('/summary') && !request.url.includes('/related?') && !request.url.includes('/airing?'));

test('sync to tracker saves the draft then reloads', async () => {
    let polls = 0, saved = false;
    const env = setup({ routes: {
        'progress/edit/op-1': () => ++polls < 3 ? { body: { operationId: 'op-1', finished: false, results: [] } }
            : (saved = true, { body: { operationId: 'op-1', finished: true, results: [{ provider: 'AniList', entryId: '1', result: 'Saved', current: null, failure: null }] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } }),
        'progress/series/': () => ({ body: seriesView(saved ? { seasons: [{ ...seriesView().seasons[0], trackers: [{ ...seriesView().seasons[0].trackers[0], entries: [entryOf(1, { progress: 4 })] }] }] } : {}) })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    await flush();
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, false);
    env.doc.querySelector('.doki-sync-up').click();
    await flush();
    assert.ok(env.doc.querySelector('.doki-editor-status .doki-popup-sync-spinner'));
    const [post] = posts(env, 'progress/edit');
    assert.deepEqual(post.body, { itemId: seriesId, edits: [{ provider: 'AniList', entryId: 1, alternativeId: null, shown: { status: 'Watching', progress: 3, score: null },
        status: 'Watching', progress: 4, score: null, clearScore: false }] });
    env.doc.querySelector('#dokiPopupMatchesTab').click();
    const disclosure = env.doc.querySelector('#dokiPopupMatches summary');
    disclosure.focus();
    await env.win.advance(5000);
    const refreshedDisclosure = env.doc.querySelector('#dokiPopupMatches summary');
    assert.notEqual(refreshedDisclosure, disclosure);
    assert.equal(env.doc.activeElement, refreshedDisclosure);
    env.doc.querySelector('#dokiPopupOverviewTab').click();
    assert.equal(polls, 3);
    assert.equal(gets(env, 'progress/series/').length, 2, 'the view is read again once');
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, true);
    assert.equal(env.doc.querySelector('[data-focus-key="pill:AniList"] .doki-pill-count').textContent, '4/26');
    popup.close({ replace: true });
    popup.open(seriesId);
    assert.equal(env.doc.querySelector('[data-focus-key="pill:AniList"] .doki-pill-count').textContent, '4/26');
    assert.equal(gets(env, 'progress/series/').length, 2);
    popup.dispose();
});

test('a raise for a clean tracker waits for each sync', async () => {
    const states = { 'op-a': ['Running', 'Succeeded'], 'op-k': ['Succeeded'] };
    const env = setup({ routes: {
        'user/operations/': ({ url }) => { const id = url.split('/').pop(); return { body: { operationId: id, state: states[id].length > 1 ? states[id].shift() : states[id][0] } }; },
        'sync-to-trackers': ({ init }) => ({ status: 202, body: { operationId: JSON.parse(init.body).provider === 'AniList' ? 'op-a' : 'op-k' } }),
        'progress/series/': () => ({ body: twoTrackers({ jellyfinProgress: 5 }, { jellyfinProgress: 5 }) })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'all').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await flush();
    assert.deepEqual(posts(env, 'sync-to-trackers').map(request => request.body.provider), ['AniList']);
    await env.win.advance(1000);
    assert.deepEqual(posts(env, 'sync-to-trackers').map(request => request.body.provider), ['AniList'], 'Kitsu waits while AniList runs');
    await env.win.advance(1000);
    assert.deepEqual(posts(env, 'sync-to-trackers').map(request => request.body.provider), ['AniList', 'Kitsu']);
    assert.deepEqual(posts(env, 'sync-to-trackers')[0].body, { itemId: seriesId, provider: 'AniList' });
    popup.dispose();
});

test('a 409 waits for the running sync and retries once', async () => {
    let attempts = 0;
    const env = setup({ routes: {
        'user/operations/': ({ url }) => ({ body: { operationId: url.split('/').pop(), state: 'Succeeded' } }),
        'sync-to-trackers': () => ++attempts === 1 ? { status: 409, body: { operationId: 'busy' } } : { status: 202, body: { operationId: 'mine' } },
        'progress/series/': () => ({ body: seriesView({ seasons: [{ ...seriesView().seasons[0], trackers: [{ ...seriesView().seasons[0].trackers[0], entries: [entryOf(1, { jellyfinProgress: 5 })] }] }] }) })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(3000);
    assert.equal(posts(env, 'sync-to-trackers').length, 2);
    assert.ok(gets(env, 'user/operations/busy').length >= 1);
    assert.ok(gets(env, 'user/operations/mine').length >= 1);
    popup.dispose();
});

test('a stale result shows the card and keeps the draft', async () => {
    const env = setup({ routes: {
        'progress/edit/op-1': () => ({ body: { operationId: 'op-1', finished: true, results: [{ provider: 'AniList', entryId: '1', result: 'Stale',
            current: { status: 'Watching', progress: 6, score: null }, failure: null }] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(2000);
    const card = env.doc.querySelector('.doki-card');
    assert.equal(card.querySelector('.doki-card-heading').textContent, 'AniList changed while this was open');
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, false);
    card.querySelector('[data-action=load]').click();
    await flush();
    assert.ok(env.doc.querySelector('.doki-card') === null);
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, true);
    popup.dispose();
});

test('polling gives up after a minute', async () => {
    const env = setup({ routes: {
        'progress/edit/op-1': () => ({ body: { operationId: 'op-1', finished: false, results: [] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(61000);
    assert.equal(gets(env, 'progress/edit/op-1').length, 60);
    assert.equal(env.doc.querySelector('.doki-editor-status').textContent, 'Still saving. Check History in AniDōki settings');
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, false);
    popup.dispose();
});

test('the expanded pill shows the editor and sync buttons with reasons', async () => {
    const env = setup();
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    const open = env.doc.querySelector('.doki-pill-open');
    assert.equal(open.querySelector('[data-focus-key="pill:AniList"]').getAttribute('aria-expanded'), 'true');
    assert.ok(env.doc.activeElement === open.querySelector('[data-focus-key="pill:AniList"]'), 'focus moves to the open pill');
    const up = env.doc.querySelector('.doki-sync-up');
    assert.equal(up.getAttribute('aria-disabled'), 'true');
    assert.equal(env.doc.getElementById(up.getAttribute('aria-describedby')).textContent, 'Up to date');
    assert.equal(env.doc.querySelector('.doki-popup-open-tracker').getAttribute('href'), 'https://anilist.co/anime/1');
    open.querySelector('[data-focus-key="pill:AniList"]').click();
    await flush();
    assert.ok(env.doc.querySelector('.doki-pill-open') === null);
    assert.ok(env.doc.activeElement === pill(env.doc, 'AniList'), 'focus stays on the collapsed pill');
    popup.dispose();
});

test('a click right after typing a rating reaches its button and keeps the rating', async () => {
    const env = setup();
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    const rating = env.doc.querySelector('[data-focus-key="rating"]');
    rating.focus();
    rating.value = '8';
    // The browser's order: the pointer goes down on +, the rating commits as it loses focus, then the pointer comes up.
    const plus = env.doc.querySelector('[data-focus-key="plus"]');
    // As in Safari, the pressed button does not take focus.
    env.doc.dispatchEvent(new Event('pointerdown'));
    rating.blur();
    rating.dispatchEvent(new Event('change'));
    env.doc.dispatchEvent(new Event('pointerup'));
    // A button replaced while the pointer was down never receives the click.
    assert.ok(env.doc.body.contains(plus), 'the button under the pointer stays in place');
    plus.click();
    await env.win.advance(0);
    assert.equal(env.doc.querySelector('[data-focus-key="pill:AniList"] .doki-pill-count').textContent, '4/26');
    assert.equal(env.doc.querySelector('[data-focus-key="rating"]').value, '8');
    assert.equal(env.doc.activeElement, env.doc.querySelector('[data-focus-key="plus"]'), 'focus stays on the clicked button');
    popup.dispose();
});

test('a reused detail page drops the previous item button', async () => {
    const env = setup();
    const answers = { [seriesId]: 204, other: 404 };
    const fetch = env.win.fetch;
    env.win.fetch = async (url, init) => {
        const match = url.match(/series\/([^/]+)\/available/);
        if (match)
            return { ok: answers[match[1]] < 300, status: answers[match[1]], json: async () => null };
        return fetch(url, init);
    };
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    assert.equal(env.row.querySelectorAll('.doki-item-button').length, 1);
    env.win.setHash('#/details?id=other');
    env.doc.dispatchEvent(new Event('viewshow'));
    await flush();
    assert.equal(env.row.querySelectorAll('.doki-item-button').length, 0, 'a non-anime item has no button');
    env.win.setHash(`#/details?id=${seriesId}`);
    await flush();
    assert.equal(env.row.querySelector('.doki-item-button').dataset.itemId, seriesId);
    popup.dispose();
});

// --- Final review fixes ---
const seasonOf = (number, entry) => ({ sourceKey: `bebop:${number}`, season: number, episodes: [1, 2, 3], watched: [1, 2, 3], lastPlayedUtc: null,
    trackers: [{ provider: 'AniList', origin: 'Automatic', unresolved: null, error: null, entries: [entry] }] });
const memoryStorage = (items = {}) => ({ getItem: key => items[key] ?? null, setItem: (key, value) => { items[key] = String(value); }, removeItem: key => { delete items[key]; }, items });

test('tab from the last control wraps to the first', async () => {
    const env = setup();
    const popup = await openPopup(env);
    const panel = dialog(env.doc), items = [...panel.querySelectorAll('button, a[href], input, select, textarea, summary, [tabindex="0"]')]
        .filter(item => !item.hidden && !item.closest('[hidden]') && item.getAttribute('tabindex') !== '-1');
    items.at(-1).focus();
    const forward = keydown(panel, 'Tab');
    assert.equal(forward.defaultPrevented, true);
    assert.ok(env.doc.activeElement === items[0], 'focus wraps to the first control');
    const backward = keydown(panel, 'Tab', { shiftKey: true });
    assert.equal(backward.defaultPrevented, true);
    assert.ok(env.doc.activeElement === items.at(-1), 'shift+tab wraps to the last control');
    popup.dispose();
});

test('arrow keys and enter pick a season', async () => {
    const env = setup({ routes: { 'progress/series/': () => ({ body: seriesView({ seasons: [seasonOf(1, entryOf(1)), seasonOf(2, entryOf(2, { title: 'Season two' }))] }) }) } });
    const popup = await openPopup(env);
    const list = env.doc.querySelector('.doki-popup-seasons [role=listbox]');
    list.querySelector('[aria-selected=true]').focus();
    keydown(list, 'ArrowDown');
    keydown(list, 'Enter');
    await flush();
    const selected = env.doc.querySelector('.doki-popup-seasons [aria-selected=true]');
    assert.equal(selected.dataset.rowKey, 'bebop:2:1');
    popup.dispose();
});

test('the editor is locked while a sync runs', async () => {
    const env = setup({ routes: {
        'progress/edit/op-1': () => ({ body: { operationId: 'op-1', finished: false, results: [] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await flush();
    assert.equal(env.doc.querySelector('[aria-label="One episode more"]').disabled, true);
    assert.equal(env.doc.querySelector('.doki-pill-open select').disabled, true);
    popup.dispose();
});

test('restored drafts the tracker already has are not unsaved', async () => {
    const env = setup();
    env.win.sessionStorage = memoryStorage({ [`anidoki:drafts:user:${seriesId}`]: JSON.stringify({ 'bebop:1:1': { AniList: {
        status: 'Watching', progress: 3, score: null, clearScore: false, shown: { status: 'Watching', progress: 2, score: null } } } }) });
    const popup = await openPopup(env);
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, true);
    popup.dispose();
});

test('a reload that drops the selected row falls back to the default row', async () => {
    let reads = 0;
    const env = setup({ routes: {
        'user/operations/': ({ url }) => ({ body: { operationId: url.split('/').pop(), state: 'Succeeded' } }),
        'sync-to-trackers': () => ({ status: 202, body: { operationId: 'op' } }),
        'progress/series/': () => ++reads === 1
            ? { body: seriesView({ seasons: [{ ...seriesView().seasons[0], trackers: [{ ...seriesView().seasons[0].trackers[0], entries: [entryOf(1, { jellyfinProgress: 5 })] }] }] }) }
            : { body: seriesView({ seasons: [{ ...seriesView().seasons[0], episodes: [2, 3] }] }) }
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(2000);
    assert.ok(env.doc.querySelector('.doki-pill'), 'the pills still show');
    assert.ok(env.doc.querySelector('.doki-popup-loading') === null);
    popup.dispose();
});

test('all trackers closes when a reload leaves one tracker', async () => {
    let reads = 0;
    const env = setup({ routes: {
        'user/operations/': ({ url }) => ({ body: { operationId: url.split('/').pop(), state: 'Succeeded' } }),
        'sync-to-trackers': () => ({ status: 202, body: { operationId: 'op' } }),
        'progress/series/': () => {
            if (++reads === 1)
                return { body: twoTrackers({ jellyfinProgress: 5 }, { jellyfinProgress: 5 }) };
            const view = twoTrackers();
            view.seasons[0].trackers[1] = { ...view.seasons[0].trackers[1], error: 'RateLimited', entries: [] };
            return { body: view };
        }
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'all').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(3000);
    assert.ok(env.doc.querySelector('.doki-pill-open') === null);
    assert.ok(pill(env.doc, 'all') === null);
    assert.ok(env.doc.querySelectorAll('.doki-pill-count').length > 0);
    for (const count of env.doc.querySelectorAll('.doki-pill-count'))
        assert.doesNotMatch(count.textContent, /Infinity/);
    popup.dispose();
});

test('a failed movie raise says so', async () => {
    const movie = seriesView({ isMovie: true, seasons: [{ sourceKey: 'movie:m', season: null, episodes: [1], watched: [1], lastPlayedUtc: null,
        trackers: [{ provider: 'AniList', origin: 'Automatic', unresolved: null, error: null, entries: [entryOf(1, { totalEpisodes: 1, firstEpisode: null,
            lastEpisode: null, status: 'PlanToWatch', progress: 0, jellyfinProgress: 1 })] }] }] });
    const env = setup({ routes: {
        'progress/edit/op-1': () => ({ body: { operationId: 'op-1', finished: true, results: [{ provider: 'AniList', entryId: '1', result: 'Failed', current: null, failure: 'transient' }] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } }),
        'progress/series/': () => ({ body: movie })
    } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    await flush();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(2000);
    assert.equal(env.doc.querySelector('.doki-editor-status').textContent, "Couldn't reach AniList. Try again in a moment");
    popup.dispose();
});

const textTree = element => element.textContent + element.children.map(textTree).join(' ');

test('Related and airing are independent of the progress editor', async () => {
    let finishRelated;
    const pending = new Promise(resolve => { finishRelated = resolve; });
    const env = setup({ routes: {
        '/related?': () => pending,
        '/airing?': () => ({ body: { airing: { episode: 8, airsAtUtc: '2099-01-07T12:00:00Z' } } })
    } });
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    const pill = panel.querySelector('[data-provider=AniList]');
    pill.focus();
    assert.ok(pill);
    assert.match(textTree(panel.querySelector('.doki-popup-details')), /Episode 8/);
    assert.equal(panel.querySelector('.doki-popup-related').hidden, true);
    finishRelated({ body: { relations: [
        { kind: 'Sequel', entryId: 2, title: 'Next season', coverUrl: 'https://example.test/cover.jpg', libraryMatches: [{ season: 2 }] },
        { kind: 'SideStory', title: 'Movie', libraryMatches: [{ season: null }] },
        { kind: 'Other', title: null, libraryMatches: [] }
    ] } });
    await flush();
    assert.match(textTree(panel.querySelector('.doki-popup-related')), /Next season.*In your library \(Season 2\)/s);
    const card = panel.querySelector('a.doki-popup-relation-card');
    assert.equal(card.getAttribute('href'), 'https://anilist.co/anime/2');
    assert.ok(card.querySelector('.doki-popup-relation-art .doki-popup-relation-logo'));
    assert.equal(card.querySelector('.doki-popup-relation-title').tagName, 'SPAN');
    assert.match(textTree(panel.querySelector('.doki-popup-related')), /Untitled entry.*Not in your library/s);
    assert.equal(env.doc.activeElement, pill);
    popup.dispose();
});

test('Matches renders source and target ranges and keeps the inactive tab out of focus', async () => {
    const view = seriesView();
    view.seasons[0].trackers[0].matchRanges = [{ sourceStart: 1, sourceEnd: 3, targetStart: 13, targetEnd: 15, entry: { title: 'Cowboy Bebop' } }];
    const env = setup({ routes: { 'progress/series/': () => ({ body: view }) } });
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    const overview = panel.querySelector('#dokiPopupOverviewTab');
    const matches = panel.querySelector('#dokiPopupMatchesTab');
    overview.focus();
    keydown(overview, 'ArrowRight');
    assert.equal(env.doc.activeElement, matches);
    assert.equal(overview.getAttribute('tabindex'), '-1');
    assert.equal(matches.getAttribute('aria-selected'), 'true');
    assert.equal(panel.querySelector('#dokiPopupOverview').hidden, true);
    assert.match(textTree(panel.querySelector('#dokiPopupMatches')), /Season 1.*Episodes 1–3.*Cowboy Bebop.*Episodes 13–15.*Automatic/s);
    const href = new URL(panel.querySelector('#dokiPopupMatches a').getAttribute('href'));
    const route = new URLSearchParams(href.hash.split('?')[1]);
    assert.equal(route.get('pageUrl'), '/AniDoki/settings');
    assert.equal(route.get('dokiSection'), 'trackerMatchesTitle');
    assert.equal(route.get('dokiSource'), 'bebop:1');
    assert.equal(href.searchParams.has('anidoki'), false);
    keydown(matches, 'Home');
    assert.equal(env.doc.activeElement, overview);
    assert.equal(panel.querySelector('#dokiPopupMatches').hidden, true);
    popup.dispose();
});

test('a late Related result cannot replace the selected season or trigger duplicate reads', async () => {
    let finishFirst;
    const first = new Promise(resolve => { finishFirst = resolve; });
    const view = seriesView({ seasons: [seasonOf(1, entryOf(1)), seasonOf(2, entryOf(2))] });
    const env = setup({ routes: {
        '/related?': ({ url }) => url.includes('entryId=1') ? first : { body: { relations: [{ title: 'Second season relation' }] } },
        '/airing?': () => ({ body: { airing: { episode: 5, airsAtUtc: '2000-01-01T00:00:00Z' } } }),
        'progress/series/': () => ({ body: view })
    } });
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    const list = panel.querySelector('.doki-popup-seasons [role=listbox]');
    list.querySelector('[aria-selected=true]').focus();
    keydown(list, 'ArrowDown');
    keydown(list, 'Enter');
    await flush();
    panel.querySelector('#dokiPopupMatchesTab').click();
    panel.querySelector('#dokiPopupOverviewTab').click();
    finishFirst({ body: { relations: [{ title: 'Stale first season relation' }] } });
    await flush();
    assert.match(textTree(panel.querySelector('.doki-popup-related')), /Second season relation/);
    assert.doesNotMatch(textTree(panel.querySelector('.doki-popup-related')), /Stale first season relation/);
    assert.doesNotMatch(textTree(panel.querySelector('.doki-popup-details')), /Episode 5/);
    assert.equal(env.requests.filter(request => request.url.includes('/related?')).length, 2);
    popup.dispose();
});

test('Related uses the exact entry of a split row with an episode exception', async () => {
    const view = seriesView();
    const tracker = view.seasons[0].trackers[0];
    tracker.entries = [entryOf(1, { firstEpisode: 1, lastEpisode: 3 }), entryOf(2, { firstEpisode: 2, lastEpisode: 2 })];
    tracker.matchRanges = [
        { sourceStart: 1, sourceEnd: 1, entry: { id: 1, title: 'Main' } },
        { sourceStart: 2, sourceEnd: 2, entry: { id: 2, title: 'Exception' } },
        { sourceStart: 3, sourceEnd: 3, entry: { id: 1, title: 'Main' } }
    ];
    const env = setup({ routes: {
        '/related?': () => ({ body: { relations: [] } }),
        '/airing?': () => ({ body: { airing: null } }),
        'progress/series/': () => ({ body: view })
    } });
    const popup = await openPopup(env);
    const list = dialog(env.doc).querySelector('.doki-popup-seasons [role=listbox]');
    list.querySelector('[aria-selected=true]').focus();
    keydown(list, 'ArrowDown');
    keydown(list, 'Enter');
    await flush();
    assert.match(env.requests.filter(request => request.url.includes('/related?')).at(-1).url, /entryId=2/);
    assert.match(env.requests.filter(request => request.url.includes('/airing?')).at(-1).url, /aniListEntryId=2/);
    popup.dispose();
});

test('the Matches Settings link keeps the AniDōki menu route among other plugin pages', async () => {
    const env = setup();
    for (const pageUrl of ['/TaskTimeline/settings', '/jf/AniDoki/settings']) {
        const link = env.doc.createElement('a');
        link.dataset.pluginPages = 'true';
        link.setAttribute('href', '#/userpluginsettings.html?pageUrl=' + pageUrl);
        env.doc.body.append(link);
    }
    const popup = await openPopup(env);
    const href = new URL(dialog(env.doc).querySelector('#dokiPopupMatches a').getAttribute('href'));
    assert.match(href.hash, /^#\/userpluginsettings\.html\?/);
    const route = new URLSearchParams(href.hash.split('?')[1]);
    assert.equal(route.get('pageUrl'), '/jf/AniDoki/settings');
    assert.equal(route.get('dokiSection'), 'trackerMatchesTitle');
    assert.equal(route.get('dokiSource'), 'bebop:1');
    popup.dispose();
});

test('Matches shows shared and personal origins when the Settings link is unavailable', async () => {
    const shared = seasonOf(0, entryOf(1)), personal = seasonOf(2, entryOf(2));
    for (const [season, origin] of [[shared, 'Shared'], [personal, 'Personal']]) {
        const tracker = season.trackers[0];
        tracker.origin = origin;
        tracker.matchRanges = [{ sourceStart: 1, sourceEnd: 1, targetStart: 1, targetEnd: 1, entry: tracker.entries[0] }];
    }
    const env = setup({ routes: { 'progress/series/': () => ({ body: seriesView({ matchesLinkAvailable: false, seasons: [shared, personal] }) }) } });
    const popup = await openPopup(env);
    const matches = dialog(env.doc).querySelector('#dokiPopupMatches');
    assert.equal(matches.querySelector('a'), null);
    assert.match(textTree(matches), /Your server admin manages these matches.*2 seasons.*Specials.*Shared default.*Season 2.*Your override/s);
    popup.dispose();
});

test('the Settings gear removes the popup parameter before navigating', async () => {
    const env = setup();
    const menu = env.doc.createElement('a');
    menu.dataset.pluginPages = 'true';
    menu.setAttribute('href', '#/userpluginsettings.html?pageUrl=/AniDoki/settings');
    env.doc.body.append(menu);
    const popup = await openPopup(env);
    let destination;
    env.win.location.assign = href => { destination = new URL(href); };
    dialog(env.doc).querySelector('.doki-popup-settings').click();
    assert.equal(dialog(env.doc), null);
    assert.equal(new URL(env.win.location.href).searchParams.has('anidoki'), false);
    assert.equal(destination.searchParams.has('anidoki'), false);
    assert.match(destination.hash, /^#\/userpluginsettings\.html\?/);
    popup.dispose();
});

test('the popup makes the Jellyfin background inert until it closes', async () => {
    const env = setup();
    const root = env.doc.createElement('div');
    root.id = 'reactRoot';
    env.doc.body.append(root);
    const popup = await openPopup(env);
    assert.equal(root.hasAttribute('inert'), true);
    const wheel = new Event('wheel', { cancelable: true });
    dialog(env.doc).parentElement.dispatchEvent(wheel);
    assert.equal(wheel.defaultPrevented, true);
    popup.close({ replace: true });
    await env.win.advance(250);
    assert.equal(root.hasAttribute('inert'), false);
    popup.dispose();
});

test('the Matches shortcut follows the selected season and carries its key through a stripped route', async () => {
    const view = seriesView({ seasons: [seasonOf(1, entryOf(1)), seasonOf(2, entryOf(2))] });
    const env = setup({ routes: { 'progress/series/': () => ({ body: view }) } });
    env.win.sessionStorage = memoryStorage();
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    const list = panel.querySelector('.doki-popup-seasons [role=listbox]');
    list.querySelector('[aria-selected=true]').focus();
    keydown(list, 'ArrowDown');
    keydown(list, 'Enter');
    panel.querySelector('#dokiPopupMatchesTab').click();
    const link = panel.querySelector('#dokiPopupMatches a');
    const url = new URL(link.getAttribute('href'));
    assert.equal(new URLSearchParams(url.hash.split('?')[1]).get('dokiSource'), 'bebop:2');
    env.win.location.assign = () => {};
    link.click();
    const pending = JSON.parse(env.win.sessionStorage.items['anidoki.openTrackerMatches']);
    assert.equal(pending.sourceKey, 'bebop:2');
    assert.equal(dialog(env.doc), null);
    popup.dispose();
});

test('an immediate reopen shows the series and its supplemental reads without another request', async () => {
    const env = setup({ routes: {
        '/related?': () => ({ body: { relations: [{ title: 'A cached sequel' }] } }),
        '/airing?': () => ({ body: { airing: null } })
    } });
    const popup = await openPopup(env);
    const requests = env.requests.length;
    popup.close({ replace: true });
    popup.open(seriesId);
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    assert.ok(pill(env.doc, 'AniList'));
    assert.match(textTree(dialog(env.doc).querySelector('.doki-popup-related')), /A cached sequel/);
    assert.equal(dialog(env.doc).querySelector('.doki-overview-skeleton'), null);
    await flush();
    assert.equal(env.requests.length, requests);
    popup.dispose();
});

test('reopening before the initial read completes joins the same request', async () => {
    let finishRead;
    const pending = new Promise(resolve => { finishRead = resolve; });
    const env = setup({ routes: { 'progress/series/': () => pending } });
    const popup = await openPopup(env);
    popup.close({ replace: true });
    popup.open(seriesId);
    assert.equal(gets(env, 'progress/series/').length, 1);
    finishRead({ body: seriesView() });
    await flush();
    assert.ok(pill(env.doc, 'AniList'));
    popup.dispose();
});

test('expired progress and progress loaded by another user are read again', async t => {
    t.mock.timers.enable({ apis: ['Date'], now: new Date('2026-11-25T12:00:00Z') });
    let user = 'first';
    const env = setup({ routes: { 'progress/series/': () => ({ body: seriesView({ name: user }) }) } });
    env.win.ApiClient.getCurrentUserId = () => user;
    const popup = await openPopup(env);
    popup.close({ replace: true });
    t.mock.timers.tick(24 * 60 * 60_000 - 1);
    popup.open(seriesId);
    assert.equal(gets(env, 'progress/series/').length, 1);
    popup.close({ replace: true });
    t.mock.timers.tick(1);
    popup.open(seriesId);
    await flush();
    assert.equal(gets(env, 'progress/series/').length, 2);

    popup.close({ replace: true });
    user = 'second';
    popup.open(seriesId);
    assert.ok(dialog(env.doc).querySelector('.doki-overview-skeleton'));
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'second');
    assert.equal(gets(env, 'progress/series/').length, 3);
    popup.dispose();
});

test('a reopen during a queued edit reads again instead of showing a cached view', async () => {
    const env = setup({ routes: { 'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } }) } });
    const popup = await openPopup(env);
    pill(env.doc, 'AniList').click();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    env.doc.querySelector('.doki-sync-up').click();
    await flush();
    assert.equal(posts(env, 'progress/edit').length, 1);
    popup.close({ replace: true });
    popup.open(seriesId);
    await flush();
    assert.equal(gets(env, 'progress/series/').length, 2);
    popup.close({ replace: true });
    popup.open(seriesId);
    await flush();
    assert.equal(gets(env, 'progress/series/').length, 3);
    popup.dispose();
});

test('ordinary playback reuses cached progress, while a confirmed change invalidates it', async () => {
    const env = setup();
    env.win.ApiClient.subscribe = () => {
        assert.fail('The popup should not listen to general Jellyfin user-data updates');
    };
    const popup = await openPopup(env);
    popup.close({ replace: true });
    const video = env.doc.createElement('video');
    for (const type of ['playing', 'pause', 'ended']) {
        const event = new Event(type);
        Object.defineProperty(event, 'target', { value: video });
        env.doc.dispatchEvent(event);
    }
    popup.open(seriesId);
    await flush();
    assert.equal(gets(env, 'progress/series/').length, 1);
    popup.close({ replace: true });
    env.doc.dispatchEvent(new Event('anidoki:progress-changed'));
    popup.open(seriesId);
    await flush();
    assert.equal(gets(env, 'progress/series/').length, 2);
    popup.dispose();
});

const libraryView = (selectedProvider = 'AniList', title = 'Cowboy Bebop') => ({
    selectedProvider,
    providers: [{ provider: 'AniList', available: true }, { provider: 'Kitsu', available: true }, { provider: 'Simkl', available: false }],
    rows: selectedProvider ? [{ itemId: seriesId, title, isMovie: false, year: 1998, hasPoster: true,
        matchState: 'OnList', status: 'Watching', progress: 3, totalEpisodes: 26, episodeCount: 6, unloggedLibraryCount: 3 }] : []
});

test('library deep link opens a card in the same dialog and Back restores its view and focus', async () => {
    const env = setup({ href: 'http://jf/web/index.html?anidoki=library#/home', routes: {
        '/library': () => ({ body: libraryView() })
    } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Your anime');
    assert.equal(gets(env, 'progress/series/').length, 0);
    panel.querySelector('.doki-library-layout-toggle').click();
    panel.querySelector('.doki-library-card').click();
    await flush();
    assert.equal(dialog(env.doc), panel);
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    assert.equal(panel.querySelector('.doki-popup-library-back').textContent, 'Your anime');
    env.win.history.back();
    await flush();
    assert.equal(dialog(env.doc), panel);
    assert.ok(panel.querySelector('.doki-library-results.is-list'));
    assert.equal(env.doc.activeElement, panel.querySelector('.doki-library-card'));
    assert.equal(gets(env, '/library').length, 1);
    keydown(panel, 'Escape');
    await env.win.advance(250);
    assert.equal(dialog(env.doc), null);
    assert.equal(env.win.location.hash, '#/home');
    popup.dispose();
});

test('the library keeps the active provider after a late response and retries a failed refresh', async () => {
    let finishKitsu, refreshes = 0;
    const kitsuPending = new Promise(resolve => { finishKitsu = resolve; });
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: {
        '/library': ({ url }) => {
            const params = new URL(url, 'http://jf').searchParams;
            if (params.get('provider') === 'Kitsu')
                return kitsuPending;
            if (params.has('refresh') && ++refreshes === 1)
                return { status: 503, body: null };
            return { body: libraryView('AniList', refreshes ? 'Updated Bebop' : 'Cowboy Bebop') };
        }
    } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    const menu = () => panel.querySelector('[role="menu"][aria-label="Tracker"]');
    const select = index => {
        if (menu().hidden)
            panel.querySelector('button[aria-label="Tracker"]').click();
        menu().querySelectorAll('[role="menuitemradio"]')[index].click();
    };
    select(2);
    assert.equal(gets(env, '/library').length, 1, 'unavailable trackers do not make requests');
    select(1);
    assert.equal(gets(env, '/library').length, 2);
    select(0);
    finishKitsu({ body: libraryView('Kitsu', 'Stale Kitsu row') });
    await flush();
    assert.match(panel.querySelector('.doki-library-card').getAttribute('aria-label'), /^Cowboy Bebop/);
    panel.querySelector('.doki-library-refresh').click();
    await flush();
    assert.match(textTree(panel.querySelector('.doki-library-notice')), /Could not read the tracker list/);
    assert.ok(panel.querySelector('.doki-library-card'), 'retain previous rows on refresh failure');
    panel.querySelector('.doki-library-notice button').click();
    await flush();
    assert.match(panel.querySelector('.doki-library-card').getAttribute('aria-label'), /^Updated Bebop/);
    assert.equal(refreshes, 2);
    popup.dispose();
});

test('returning from a synced series refreshes the selected tracker list once', async () => {
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: {
        '/library': ({ url }) => ({ body: libraryView('AniList', url.includes('refresh=true') ? 'Updated Bebop' : 'Cowboy Bebop') }),
        'progress/edit/op-1': () => ({ body: { operationId: 'op-1', finished: true, results: [
            { provider: 'AniList', entryId: '1', result: 'Saved', current: null }
        ] } }),
        'progress/edit': () => ({ status: 202, body: { operationId: 'op-1' } })
    } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    panel.querySelector('.doki-library-card').click();
    await flush();
    pill(env.doc, 'AniList').click();
    env.doc.querySelector('[aria-label="One episode more"]').click();
    env.doc.querySelector('.doki-sync-up').click();
    await env.win.advance(1500);
    panel.querySelector('.doki-popup-library-back').click();
    await flush();
    assert.match(panel.querySelector('.doki-library-card').getAttribute('aria-label'), /^Updated Bebop/);
    assert.equal(gets(env, '/library').length, 2);
    assert.match(gets(env, '/library')[1].url, /provider=AniList&refresh=true/);
    env.win.history.forward();
    await flush();
    assert.ok(panel.querySelector('.doki-popup-library-back'));
    keydown(panel, 'Escape');
    await flush();
    assert.equal(gets(env, '/library').length, 2);
    popup.dispose();
});

test('search and view changes render 200 library titles without requesting series or tracker data', async () => {
    const view = libraryView();
    view.rows = Array.from({ length: 200 }, (_, index) => ({
        ...view.rows[0], itemId: 'test-item-' + index, title: 'Library title ' + String(index).padStart(3, '0')
    }));
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: { '/library': () => ({ body: view }) } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    assert.equal(panel.querySelectorAll('.doki-library-card').length, 200);
    panel.querySelector('.doki-library-layout-toggle').click();
    assert.equal(panel.querySelectorAll('.doki-library-card').length, 200);
    panel.querySelector('.doki-library-search-toggle').click();
    const input = panel.querySelector('input');
    input.value = 'Library title 010';
    input.dispatchEvent(new Event('input'));
    assert.equal(panel.querySelectorAll('.doki-library-card').length, 1);
    assert.equal(gets(env, '/library').length, 1);
    assert.equal(gets(env, 'progress/series/').length, 0);
    popup.dispose();
});

test('immediate browser Forward reopens the library during the close animation', async () => {
    const env = setup({ routes: { '/library': () => ({ body: libraryView() }) } });
    const popup = startProgressPopup(env.win, env.doc);
    popup.open('library');
    await flush();
    env.win.history.back();
    env.win.history.forward();
    await env.win.advance(250);
    assert.equal(env.doc.getElementById('dokiPopupTitle')?.textContent, 'Your anime');
    assert.ok(dialog(env.doc).querySelector('.doki-library-card'));
    popup.dispose();
});

test('a refresh that loses access hides previously loaded library titles', async () => {
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: {
        '/library': ({ url }) => url.includes('refresh=true') ? { status: 401, body: null } : { body: libraryView() }
    } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    panel.querySelector('.doki-library-refresh').click();
    await flush();
    assert.match(textTree(panel.querySelector('.doki-library-notice')), /Sign in to see your anime/);
    assert.equal(panel.querySelector('.doki-library-card'), null);
    popup.dispose();
});

test('Escape dismisses the tracker-links menu before returning to the library', async () => {
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: {
        '/library': () => ({ body: libraryView() }),
        'progress/series/': () => ({ body: twoTrackers() })
    } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    panel.querySelector('.doki-library-card').click();
    await flush();
    panel.querySelector('button.doki-popup-open-tracker').click();
    keydown(panel, 'Escape');
    assert.equal(panel.querySelector('button.doki-popup-open-tracker').getAttribute('aria-expanded'), 'false');
    assert.equal(env.doc.activeElement, panel.querySelector('button.doki-popup-open-tracker'));
    assert.ok(panel.querySelector('.doki-popup-library-back'));
    keydown(panel, 'Escape');
    await flush();
    assert.ok(panel.querySelector('.doki-library-card'));
    popup.dispose();
});

test('Forward after closing a deep-linked library journey restores a standalone series', async () => {
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: { '/library': () => ({ body: libraryView() }) } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    panel.querySelector('.doki-library-card').click();
    await flush();
    panel.querySelector('button.doki-popup-close').click();
    await env.win.advance(250);
    assert.equal(dialog(env.doc), null);
    assert.equal(env.win.location.search, '');
    env.win.history.forward();
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    keydown(dialog(env.doc), 'Escape');
    await env.win.advance(250);
    assert.equal(dialog(env.doc), null);
    popup.dispose();
});

test('the item-page popup breadcrumb opens Your anime without adding a history entry', async () => {
    const env = setup({ routes: { '/library': () => ({ body: libraryView() }) } });
    const popup = await openPopup(env);
    const panel = dialog(env.doc);
    const route = env.win.location.hash;
    const historyLength = env.win.history.length;
    panel.querySelector('.doki-popup-library-back').click();
    await flush();
    assert.equal(dialog(env.doc), panel);
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Your anime');
    assert.equal(env.win.history.length, historyLength);
    assert.equal(env.win.location.hash, route);
    env.win.history.back();
    await env.win.advance(250);
    assert.equal(dialog(env.doc), null);
    assert.equal(env.win.location.hash, route);
    popup.dispose();
});

test('a library deep link waits for Jellyfin to restore the signed-in user', async () => {
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: { '/library': () => ({ body: libraryView() }) } });
    let signedIn = false;
    env.win.ApiClient.isLoggedIn = () => signedIn;
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    assert.equal(gets(env, '/library').length, 0);
    signedIn = true;
    await env.win.advance(500);
    assert.ok(dialog(env.doc).querySelector('.doki-library-card'));
    assert.equal(gets(env, '/library').length, 1);
    popup.dispose();
});

test('unavailable matches sort last and show one short list label with the full tooltip', async () => {
    const view = libraryView();
    view.rows.unshift({ ...view.rows[0], itemId: 'missing', title: 'A missing series', matchState: 'Unresolved',
        status: null, progress: null, totalEpisodes: null, unloggedLibraryCount: null });
    const env = setup({ href: 'http://jf/web/?anidoki=library#/home', routes: { '/library': () => ({ body: view }) } });
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    const panel = dialog(env.doc);
    const [matched, unavailable] = panel.querySelectorAll('.doki-library-card');
    assert.equal(matched.dataset.itemId, seriesId);
    assert.equal(matched.querySelector('.doki-library-watched').textContent, '3');
    assert.equal(matched.querySelector('.doki-library-total').textContent, '/26');
    assert.equal(unavailable.dataset.itemId, 'missing');
    assert.ok(unavailable.classList.contains('is-unavailable'));
    assert.equal(unavailable.querySelector('.doki-library-progress'), null);
    assert.equal(unavailable.querySelector('.doki-library-match-tooltip').textContent, 'Match unavailable');
    const toggle = panel.querySelector('.doki-library-layout-toggle');
    assert.equal(toggle.querySelector('.doki-popup-chip-icon').dataset.icon, 'posters');
    toggle.click();
    assert.equal(toggle.querySelector('.doki-popup-chip-icon').dataset.icon, 'list');
    const unavailableRow = panel.querySelector('.doki-library-card.is-unavailable');
    assert.match(textTree(unavailableRow.querySelector('.doki-library-card-meta')).trim(), /^Unavailable/);
    assert.equal(unavailableRow.querySelector('.doki-library-progress'), null);
    popup.dispose();
});
