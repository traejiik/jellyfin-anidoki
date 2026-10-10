import { buildPresentation, createNotificationState, createVisibleTimer } from './notification-state.js';

// Independent from settings-page initialization; one runtime survives route changes.
const runtimeKey = '__anidokiNotifications';
export function startNotifications(win = window, doc = document) {
    function node(tag, className, text) {
        const element = doc.createElement(tag); element.className = className;
        if (text !== undefined) element.textContent = text;
        return element;
    }
    win[runtimeKey]?.dispose();
    const lifetime = new AbortController(), state = createNotificationState(), rendered = new Map();
    let host, announcement, request, requestTimer, bootstrapTimer, pollTimer, renderTimer, userKey, blockedIdentity, failures = 0, stopped = false, suspended = false, ready = false, nativeVideoFullscreen = false, bootAttempts = 0, transitionUntil = 0;
    const clearToasts = () => { win.clearInterval(renderTimer); renderTimer = null; state.clear(); rendered.clear(); host?.remove(); host = null; announcement = null; };
    const identity = api => api?.isLoggedIn?.() ? `${api.serverAddress()}:${api.getCurrentUserId()}:${api.deviceId?.()}:${api.accessToken?.()}` : '';
    function mount() {
        const full = doc.fullscreenElement ?? doc.webkitFullscreenElement;
        const unsupported = nativeVideoFullscreen || (full && ['VIDEO', 'AUDIO', 'IFRAME'].includes(full.tagName));
        if (host) { host.dataset.fullscreen = String(!!full); host.hidden = !!unsupported; if (!unsupported) (full ?? doc.body).append(host); }
        for (const item of rendered.values()) unsupported ? item.timer.pause('fullscreen') : item.timer.resume('fullscreen');
    }
    function createHost() {
        host = node('div', 'anidoki ad-notification-host');
        announcement = node('div', 'ad-sr-only'); announcement.setAttribute('role', 'status'); announcement.setAttribute('aria-live', 'polite'); announcement.setAttribute('aria-atomic', 'true');
        host.append(announcement); mount();
    }
    function dismiss(id) {
        const entry = rendered.get(id);
        if (entry?.root.contains(doc.activeElement)) {
            if (entry.previous?.isConnected && typeof entry.previous.focus === 'function') entry.previous.focus({ preventScroll: true });
        }
        entry?.root.remove(); rendered.delete(id); state.dismiss(id); render();
    }
    function render() {
        if (!state.visible.length) return;
        if (!host) createHost();
        for (const event of state.visible) {
            if (rendered.has(event.eventId)) continue;
            const presentation = buildPresentation(event); if (!presentation) continue;
            const root = node('section', 'ad-toast'); const content = node('div', 'ad-toast-content');
            const label = node('div', 'ad-toast-label', `✓ ${presentation.heading}`);
            const title = node('h2', 'ad-toast-title', presentation.title);
            content.append(label, title);
            if (presentation.detail) content.append(node('p', 'ad-toast-detail', presentation.detail));
            for (const detail of presentation.details) content.append(node('p', 'ad-toast-detail', detail));
            if (presentation.warning) content.append(node('p', 'ad-toast-warning', presentation.warning));
            const close = node('button', 'ad-toast-close', '×'); close.type = 'button'; close.setAttribute('aria-label', 'Dismiss tracker update');
            close.addEventListener('click', () => dismiss(event.eventId), { signal: lifetime.signal });
            const track = node('div', 'ad-toast-countdown'); track.setAttribute('aria-hidden', 'true'); const bar = node('div', 'ad-toast-countdown-fill'); track.append(bar);
            root.append(content, close, track);
            const timer = createVisibleTimer(); if (doc.hidden) timer.pause('hidden');
            root.addEventListener('mouseenter', () => timer.pause('hover'), { signal: lifetime.signal });
            root.addEventListener('mouseleave', () => timer.resume('hover'), { signal: lifetime.signal });
            root.addEventListener('focusin', () => timer.pause('focus'), { signal: lifetime.signal });
            root.addEventListener('focusout', event => { if (!root.contains(event.relatedTarget)) timer.resume('focus'); }, { signal: lifetime.signal });
            rendered.set(event.eventId, { root, timer, bar, previous: doc.activeElement });
            host.append(root); announcement.textContent = [presentation.heading, presentation.title, presentation.detail, ...presentation.details, presentation.warning].filter(Boolean).join('. ');
        }
        for (const event of state.visible) host.append(rendered.get(event.eventId)?.root);
        mount();
        if (!renderTimer) renderTimer = win.setInterval(() => {
            if (identity(win.ApiClient) !== userKey) { resetIdentity(); return; }
            for (const [id, entry] of [...rendered]) {
                entry.bar.style.transform = `scaleX(${entry.timer.remaining() / 6000})`;
                if (entry.timer.remaining() <= 0) dismiss(id);
            }
            if (!rendered.size) { win.clearInterval(renderTimer); renderTimer = null; host?.remove(); host = null; }
        }, 100);
    }
    function resetIdentity() {
        request?.abort(); win.clearTimeout(requestTimer); requestTimer = null; request = null; clearToasts(); state.cursor = null; userKey = ''; failures = 0;
    }
    function schedule(delay) { win.clearTimeout(pollTimer); if (!stopped && !suspended) pollTimer = win.setTimeout(poll, delay); }
    async function poll() {
        if (stopped || suspended || !ready || request) return;
        const api = win.ApiClient, key = identity(api);
        if (!key) { if (userKey) resetIdentity(); schedule(5000); return; }
        if (key === blockedIdentity) { schedule(5000); return; }
        if (key !== userKey) { resetIdentity(); userKey = key; blockedIdentity = null; }
        const controller = new AbortController(); request = controller;
        let timedOut = false;
        const requestTimeout = requestTimer = win.setTimeout(() => { timedOut = true; controller.abort(); }, 15000);
        try {
            // Jellyfin's legacy ajax wrapper ignores AbortSignal. Reuse its header
            // builder with native fetch, as the settings pages do.
            const headers = { Accept: 'application/json' }; api.setRequestHeaders(headers);
            const result = await win.fetch(api.getUrl('AniDoki/user/events', state.cursor ? { cursor: state.cursor } : {}), { method: 'GET', headers, credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (!result.ok) throw result;
            const response = await result.json();
            if (stopped || controller.signal.aborted || identity(win.ApiClient) !== key) return;
            state.receive(response); failures = 0;
            if (!response.enabled || response.reset) clearToasts();
            if (response.enabled) render();
        } catch (error) {
            if (stopped || (controller.signal.aborted && !timedOut)) return;
            const status = error?.status;
            if ([404, 410].includes(status)) { dispose(); return; }
            if ([401, 403].includes(status)) { resetIdentity(); blockedIdentity = key; }
            failures = Math.min(failures + 1, 3);
        } finally {
            win.clearTimeout(requestTimeout);
            if (requestTimer === requestTimeout) requestTimer = null;
            if (request === controller) request = null;
            const delay = failures ? [5000, 10000, 30000][failures - 1] : !state.enabled ? 30000 : doc.hidden ? 15000 : Date.now() < transitionUntil || [...doc.querySelectorAll('video')].some(video => !video.paused) ? 2000 : 5000;
            schedule(delay);
        }
    }
    function navigation() {
        if (identity(win.ApiClient) !== userKey) resetIdentity(); transitionUntil = Date.now() + 120000;
        schedule(0);
    }
    function dispose() {
        if (stopped) return; stopped = true; lifetime.abort(); request?.abort(); win.clearTimeout(requestTimer); win.clearTimeout(bootstrapTimer); win.clearTimeout(pollTimer); win.clearInterval(renderTimer); clearToasts();
        doc.querySelector('[data-anidoki-notification-style]')?.remove();
        if (win[runtimeKey]?.dispose === dispose) delete win[runtimeKey];
    }
    function bootstrap() {
        if (stopped || suspended || ready) return;
        const api = win.ApiClient;
        if (!api?.getUrl || !doc.body) {
            if (++bootAttempts < 120) bootstrapTimer = win.setTimeout(bootstrap, 500);
            else dispose(); return;
        }
        win.clearTimeout(bootstrapTimer); bootstrapTimer = null;
        const style = node('link', ''); style.rel = 'stylesheet'; style.href = api.getUrl('AniDoki/assets/styles.css'); style.dataset.anidokiNotificationStyle = '';
        doc.head.append(style); ready = true; poll();
    }
    doc.addEventListener('viewshow', navigation, { signal: lifetime.signal });
    win.addEventListener('hashchange', navigation, { signal: lifetime.signal });
    doc.addEventListener('fullscreenchange', mount, { signal: lifetime.signal });
    doc.addEventListener('webkitfullscreenchange', mount, { signal: lifetime.signal });
    doc.addEventListener('webkitbeginfullscreen', () => { nativeVideoFullscreen = true; mount(); }, { capture: true, signal: lifetime.signal });
    doc.addEventListener('webkitendfullscreen', () => { nativeVideoFullscreen = false; mount(); }, { capture: true, signal: lifetime.signal });
    doc.addEventListener('visibilitychange', () => {
        for (const entry of rendered.values()) doc.hidden ? entry.timer.pause('hidden') : entry.timer.resume('hidden');
        schedule(0);
    }, { signal: lifetime.signal });
    win.addEventListener('pagehide', () => { suspended = true; request?.abort(); win.clearTimeout(requestTimer); win.clearTimeout(bootstrapTimer); win.clearTimeout(pollTimer); for (const entry of rendered.values()) entry.timer.pause('pagehide'); }, { signal: lifetime.signal });
    win.addEventListener('pageshow', () => { suspended = false; for (const entry of rendered.values()) entry.timer.resume('pagehide'); if (ready) schedule(0); else bootstrap(); }, { signal: lifetime.signal });
    win[runtimeKey] = { dispose }; bootstrap(); return win[runtimeKey];
}
if (typeof window !== 'undefined') startNotifications();
