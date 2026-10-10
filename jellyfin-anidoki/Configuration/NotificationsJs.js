import { buildPresentation, createNotificationState, createVisibleTimer } from './notification-state.js';
import { buildManualMarkPresentation, createManualMarkState } from './manual-mark-state.js';

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
    const manual = createManualMarkState(), manualRendered = new Map(), commands = new Map();
    let enrolled = false, manualUnavailable = false;
    const startedAt = Date.now();
    let host, announcement, request, requestTimer, bootstrapTimer, pollTimer, renderTimer, userKey, blockedIdentity, failures = 0, stopped = false, suspended = false, ready = false, nativeVideoFullscreen = false, bootAttempts = 0, transitionUntil = 0;
    const clearToasts = () => {
        for (const entry of rendered.values()) entry.root.remove();
        state.clear(); rendered.clear();
        if (!manualRendered.size) { win.clearInterval(renderTimer); renderTimer = null; host?.remove(); host = null; announcement = null; }
    };
    const identity = api => api?.isLoggedIn?.() ? `${api.serverAddress()}:${api.getCurrentUserId()}:${api.deviceId?.()}:${api.accessToken?.()}` : '';
    function mount() {
        const full = doc.fullscreenElement ?? doc.webkitFullscreenElement;
        const unsupported = nativeVideoFullscreen || (full && ['VIDEO', 'AUDIO', 'IFRAME'].includes(full.tagName));
        if (host) { host.dataset.fullscreen = String(!!full); host.hidden = !!unsupported; if (!unsupported) (full ?? doc.body).append(host); }
        for (const item of [...rendered.values(), ...manualRendered.values()]) unsupported ? item.timer.pause('fullscreen') : item.timer.resume('fullscreen');
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
        renderManual();
        if (!state.visible.length && !manualRendered.size) return;
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
        const space = 2 - manualRendered.size;
        state.visible.forEach((event, index) => { const entry = rendered.get(event.eventId); if (entry) { entry.root.hidden = index >= space; index >= space ? entry.timer.pause('manual-priority') : entry.timer.resume('manual-priority'); host.append(entry.root); } });
        for (const entry of manualRendered.values()) host.append(entry.root);
        mount();
        if (!renderTimer) renderTimer = win.setInterval(() => {
            if (identity(win.ApiClient) !== userKey) { resetIdentity(); return; }
            for (const [id, entry] of [...rendered]) {
                entry.bar.style.transform = `scaleX(${entry.timer.remaining() / 6000})`;
                if (entry.timer.remaining() <= 0) dismiss(id);
            }
            for (const [id, entry] of [...manualRendered]) {
                if (entry.bar) entry.bar.style.transform = `scaleX(${entry.timer.remaining() / 6000})`;
                if (entry.terminal && entry.timer.remaining() <= 0) { manual.hide(id); entry.root.remove(); manualRendered.delete(id); render(); }
            }
            if (!rendered.size && !manualRendered.size) { win.clearInterval(renderTimer); renderTimer = null; host?.remove(); host = null; }
        }, 100);
    }
    function renderManual() {
        const visible = manual.visible(), ids = new Set(visible.map(o => o.operationId));
        for (const [id, entry] of [...manualRendered]) if (!ids.has(id)) { entry.root.remove(); manualRendered.delete(id); }
        if (visible.length && !host) createHost();
        for (const operation of visible) {
            const id = operation.operationId, busy = commands.has(id), old = manualRendered.get(id);
            if (old?.revision === operation.revision && old.busy === busy) continue;
            const focused = old?.root.contains(doc.activeElement); old?.root.remove();
            const p = buildManualMarkPresentation(operation), root = node('section', 'ad-toast ad-manual-toast'), content = node('div', 'ad-toast-content');
            root.dataset.state = operation.state;
            const label = node('div', 'ad-toast-label', p.heading);
            if (p.spinner) { const spinner = node('span', 'ad-update-spinner'); spinner.setAttribute('aria-hidden','true'); label.prepend(spinner); }
            content.append(label, node('h2', 'ad-toast-title', operation.sourceTitle));
            if (operation.sourceEpisode != null || operation.episodeTitle) content.append(node('p','ad-toast-detail', [operation.sourceEpisode == null ? 'Episode number unavailable' : `Episode ${operation.sourceEpisode}`, operation.episodeTitle || 'Episode title unavailable'].join(' · ')));
            content.append(node('p', 'ad-toast-detail', p.detail));
            for (const outcome of operation.outcomes ?? []) content.append(node('p','ad-toast-detail', `${outcome.provider}: ${outcome.kind}${outcome.progress == null ? '' : ` · ${outcome.progress} episodes`}`));
            if (operation.state === 'Pending') {
                const actions = node('div','ad-toast-actions');
                for (const [text, action] of [['Update trackers','confirm'],['Dismiss','dismiss']]) {
                    const button = node('button',action === 'confirm' ? 'ad-button ad-button-primary' : 'ad-button',text); button.type='button'; button.disabled=busy || manualUnavailable;
                    button.addEventListener('click',()=>command(operation,action),{signal:lifetime.signal}); actions.append(button);
                } content.append(actions);
            }
            const close = node('button','ad-toast-close','×'); close.type='button'; close.disabled=busy;
            close.setAttribute('aria-label', ['Pending','Queued'].includes(operation.state) ? 'Dismiss tracker confirmation' : 'Close tracker update');
            close.addEventListener('click',()=>{ if(['Pending','Queued'].includes(operation.state)) command(operation,'dismiss'); else { manual.hide(id); render(); } },{signal:lifetime.signal});
            root.append(content,close);
            const timer = old?.terminal && p.terminal ? old.timer : createVisibleTimer();
            if (!p.terminal) timer.pause('active'); if(doc.hidden)timer.pause('hidden');
            let bar;
            if (p.terminal) {
                const track = node('div','ad-toast-countdown'); track.setAttribute('aria-hidden','true');
                bar = node('div','ad-toast-countdown-fill'); bar.style.transform = `scaleX(${timer.remaining() / 6000})`;
                track.append(bar); root.append(track);
            }
            root.addEventListener('mouseenter',()=>timer.pause('hover'),{signal:lifetime.signal});root.addEventListener('mouseleave',()=>timer.resume('hover'),{signal:lifetime.signal});
            root.addEventListener('focusin',()=>timer.pause('focus'),{signal:lifetime.signal});root.addEventListener('focusout',e=>{if(!root.contains(e.relatedTarget))timer.resume('focus');},{signal:lifetime.signal});
            manualRendered.set(id,{root,timer,bar,terminal:p.terminal,revision:operation.revision,busy});host.append(root);
            announcement.textContent = [p.heading,operation.sourceTitle,p.detail].join('. ');
            if(focused && typeof close.focus==='function')close.focus({preventScroll:true});
        }
    }
    async function manualPoll(api, controller, key) {
        if (manualUnavailable) return;
        const headers = {Accept:'application/json'};api.setRequestHeaders(headers);
        if(!enrolled) {
            const result=await win.fetch(api.getUrl('AniDoki/user/manual-marks/capability'),{method:'POST',headers,credentials:'same-origin',signal:controller.signal});
            if(!result.ok) { if(result.status===404) {manualUnavailable=true;return;} throw result; }
            if(controller.signal.aborted || identity(win.ApiClient)!==key)return;
            enrolled=true;
        }
        const result=await win.fetch(api.getUrl('AniDoki/user/manual-marks'),{headers,credentials:'same-origin',cache:'no-store',signal:controller.signal});
        if(!result.ok)throw result;
        const snapshot=await result.json();if(controller.signal.aborted || identity(win.ApiClient)!==key)return;
        for(const operation of snapshot.operations ?? []) {
            const recent = Date.parse(operation.expiresUtc) - 120000 >= startedAt - 15000;
            if(manual.operations.has(operation.operationId) || ['Pending','Queued','Sending'].includes(operation.state) || recent)manual.upsert(operation);
        }
        render();
    }
    async function command(operation, action) {
        const api=win.ApiClient,key=identity(api);if(key!==userKey || commands.has(operation.operationId))return;
        const controller=new AbortController();commands.set(operation.operationId,controller);render();
        const timer=win.setTimeout(()=>controller.abort(),15000);
        const headers={Accept:'application/json','Content-Type':'application/json'};api.setRequestHeaders(headers);
        try {
            const result=await win.fetch(api.getUrl(`AniDoki/user/manual-marks/${operation.operationId}/${action}`),{method:'POST',headers,credentials:'same-origin',signal:controller.signal,body:JSON.stringify({expectedRevision:operation.revision})});
            const current=await result.json();if(identity(win.ApiClient)===key && current.operationId)manual.upsert(current);
        } catch {
            // Never repeat an uncertain confirm. The next authoritative snapshot
            // recovers Queued/Sending/terminal state using the same operation ID.
        } finally { win.clearTimeout(timer);if(commands.get(operation.operationId)===controller)commands.delete(operation.operationId);if(identity(win.ApiClient)===key){render();schedule(0);} }
    }
    function resetIdentity() {
        for(const command of commands.values())command.abort();commands.clear();manual.clear();
        for(const entry of manualRendered.values())entry.root.remove();manualRendered.clear();enrolled=false;manualUnavailable=false;
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
            await manualPoll(api, controller, key);
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
            const manualActive = [...manual.operations.values()].some(o=>['Pending','Queued','Sending'].includes(o.state));
            const delay = failures ? [5000, 10000, 30000][failures - 1] : manualActive ? 2000 : !state.enabled ? 5000 : doc.hidden ? 15000 : Date.now() < transitionUntil || [...doc.querySelectorAll('video')].some(video => !video.paused) ? 2000 : 5000;
            schedule(delay);
        }
    }
    function navigation() {
        if (identity(win.ApiClient) !== userKey) resetIdentity(); transitionUntil = Date.now() + 120000;
        schedule(0);
    }
    function dispose() {
        if (stopped) return; stopped = true; lifetime.abort(); for(const command of commands.values())command.abort();commands.clear();manual.clear();for(const entry of manualRendered.values())entry.root.remove();manualRendered.clear(); request?.abort(); win.clearTimeout(requestTimer); win.clearTimeout(bootstrapTimer); win.clearTimeout(pollTimer); win.clearInterval(renderTimer); clearToasts();
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
        for (const entry of [...rendered.values(), ...manualRendered.values()]) doc.hidden ? entry.timer.pause('hidden') : entry.timer.resume('hidden');
        schedule(0);
    }, { signal: lifetime.signal });
    win.addEventListener('pagehide', () => { suspended = true; request?.abort(); win.clearTimeout(requestTimer); win.clearTimeout(bootstrapTimer); win.clearTimeout(pollTimer); for (const entry of [...rendered.values(), ...manualRendered.values()]) entry.timer.pause('pagehide'); }, { signal: lifetime.signal });
    win.addEventListener('pageshow', () => { suspended = false; for (const entry of [...rendered.values(), ...manualRendered.values()]) entry.timer.resume('pagehide'); if (ready) schedule(0); else bootstrap(); }, { signal: lifetime.signal });
    win[runtimeKey] = { dispose }; bootstrap(); return win[runtimeKey];
}
if (typeof window !== 'undefined') startNotifications();
