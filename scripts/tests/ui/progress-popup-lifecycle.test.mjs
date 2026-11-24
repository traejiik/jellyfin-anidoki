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
        const route = Object.entries(routes).find(([pattern]) => url.includes(pattern));
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
    assert.equal(dialog(env.doc), null);
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
    assert.equal(dialog(env.doc), null);
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
    assert.equal(dialog(env.doc), null);
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
    retry.click();
    await flush();
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    popup.dispose();
});

test('dispose removes everything', async () => {
    const env = setup();
    const popup = startProgressPopup(env.win, env.doc);
    await flush();
    popup.open(seriesId, {});
    await flush();
    popup.dispose();
    assert.equal(dialog(env.doc), null);
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
const gets = (env, part) => env.requests.filter(request => request.method === 'GET' && request.url.includes(part) && !request.url.includes('/available'));

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
    const [post] = posts(env, 'progress/edit');
    assert.deepEqual(post.body, { itemId: seriesId, edits: [{ provider: 'AniList', entryId: 1, alternativeId: null, shown: { status: 'Watching', progress: 3, score: null },
        status: 'Watching', progress: 4, score: null, clearScore: false }] });
    await env.win.advance(5000);
    assert.equal(polls, 3);
    assert.equal(gets(env, 'progress/series/').length, 2, 'the view is read again once');
    assert.equal(env.doc.querySelector('.doki-popup-unsaved').hidden, true);
    assert.equal(env.doc.querySelector('[data-focus-key="pill:AniList"] .doki-pill-count').textContent, '4/26');
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
    assert.equal(env.doc.querySelector('.doki-card'), null);
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
    assert.equal(env.doc.querySelector('.doki-pill-open'), null);
    assert.ok(env.doc.activeElement === pill(env.doc, 'AniList'), 'focus stays on the collapsed pill');
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
