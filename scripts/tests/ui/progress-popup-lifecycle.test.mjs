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
    assert.equal(env.doc.activeElement, env.doc.getElementById('dokiPopupTitle'));
    assert.equal(env.doc.getElementById('dokiPopupTitle').textContent, 'Cowboy Bebop');
    env.win.history.back();
    await env.win.advance(250);
    assert.equal(dialog(env.doc), null);
    assert.equal(env.doc.activeElement, button);
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
