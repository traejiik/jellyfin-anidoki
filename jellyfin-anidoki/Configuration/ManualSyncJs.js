// Outbound currently runs the user's existing provider scope. The endpoint still
// takes provider/status arguments, so supply compatibility values without
// exposing an ineffective choice in the form.
export function buildSyncRequest(form, linked) {
    if (!form.userId) throw new Error('Select a user.');
    if (!['UpdateJellyfin', 'UpdateProvider'].includes(form.syncAction)) throw new Error('Select a valid direction.');
    if (!linked.length) throw new Error('This user has no linked trackers. Link an account in Settings.');
    if (form.syncAction === 'UpdateProvider') return { userId: form.userId, provider: linked[0], syncAction: form.syncAction, status: 'Both' };
    if (!linked.includes(form.provider)) throw new Error('Select a linked provider for this user.');
    if (!form.completed && !form.watching) throw new Error('Choose at least one inbound status.');
    return { userId: form.userId, provider: form.provider, syncAction: form.syncAction, status: form.completed && form.watching ? 'Both' : form.completed ? 'Completed' : 'Watching' };
}
export const syncFeedback = success => success
    ? 'Sync request finished. Check Jellyfin logs for provider results.'
    : 'Could not confirm the outcome. Check Jellyfin logs before running again.';

// Jellyfin 12.2 matches main-menu plugin links by their exact page name. Hidden
// sibling pages otherwise select Plugins. Keep this compatibility correction
// confined to the visible manual-sync page and the existing dashboard links.
export function selectManualSyncSidebar(signal, doc = document) {
    if (signal.aborted || !doc.body || typeof MutationObserver === 'undefined') return;
    const manualPage = 'AniDoki_ManualSync', primaryPage = 'AniDoki';
    const snapshots = new Map();
    const route = href => {
        const hash = href?.slice(href.indexOf('#') + 1) ?? '';
        const [path, query] = hash.split('?');
        return { path, params: new URLSearchParams(query) };
    };
    const currentRoute = () => route(doc.defaultView?.location.hash);
    const isManual = () => {
        const current = currentRoute();
        return current.path === '/configurationpage' && current.params.get('name') === manualPage;
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
        if (signal.aborted || !isManual()) return;
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
            if (!isManual()) {
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
                if (snapshot.current === null || !isManual() && !selected) link.removeAttribute('aria-current');
                else link.setAttribute('aria-current', snapshot.current);
            }
        }
        snapshots.clear();
    }, { once: true });
    refresh();
    mountObserver.observe(doc.body, { childList: true, subtree: true });
}

export default function (view) {
    let lifetime, common, users = [], config, busy = false, lastRun;
    const find = selector => view.querySelector(selector);
    const root = view.querySelector('.anidoki');
    const links = () => (config?.UserConfig?.find(user => user.UserId === find('#selectSyncUser').value)?.UserApiAuth ?? []).map(auth => auth.Name);
    const options = () => ({ userId: find('#selectSyncUser').value, provider: find('#selectSyncProvider').value,
        syncAction: find('input[name="syncAction"]:checked').value, completed: find('#completedStatus').checked, watching: find('#watchingStatus').checked });
    view.addEventListener('viewhide', () => lifetime?.abort());
    view.addEventListener('viewshow', () => {
        lifetime?.abort(); lifetime = new AbortController(); const signal = lifetime.signal;
        selectManualSyncSidebar(signal, view.ownerDocument);
        start(signal).catch(async error => {
            const message = common ? await common.errorMessage(error) : 'Could not load AniDōki resources.';
            if (!signal.aborted) find('[data-page-status]').textContent = message;
        });
    });
    async function start(signal) {
        common = await import(ApiClient.getUrl('AniDoki/assets/common.js')); if (signal.aborted) return;
        common.ensureStyles(); common.setTabs(common.TabManualSync, common.getTabs, view);
        find('#runSync').disabled = true; common.status(find('[data-page-status]'), 'Loading users and linked trackers…');
        const result = await Promise.all([ApiClient.getUsers(), ApiClient.getPluginConfiguration(common.pluginId)]); if (signal.aborted) return;
        [users, config] = result;
        const selected = find('#selectSyncUser').value; common.populateUserList(view, users, '#selectSyncUser');
        if (users.some(user => user.Id === selected)) find('#selectSyncUser').value = selected;
        find('#ManualSyncConfigForm').hidden = false;
        common.status(find('[data-page-status]'), ''); updateProviders(); updateForm();
        if (lastRun) common.status(find('#runStatus'), lastRun.message, lastRun.tone);
        find('#selectSyncUser').addEventListener('change', () => { updateProviders(); updateForm(); }, { signal });
        view.querySelectorAll('input, select').forEach(input => input.addEventListener('change', updateForm, { signal }));
        find('#ManualSyncConfigForm').addEventListener('submit', event => { event.preventDefault(); run(); }, { signal });
    }
    function updateProviders() {
        const selected = find('#selectSyncProvider').value;
        find('#selectSyncProvider').replaceChildren();
        links().forEach(key => find('#selectSyncProvider').add(new Option(common.providerName(key), key)));
        if (links().includes(selected)) find('#selectSyncProvider').value = selected;
    }
    function updateForm() {
        const inbound = options().syncAction === 'UpdateJellyfin'; find('#inboundOptions').hidden = !inbound; find('#outboundScope').hidden = inbound;
        let problem = ''; try { buildSyncRequest(options(), links()); } catch (error) { problem = error.message; }
        common.status(find('#formStatus'), problem);
        find('#settingsLink').hidden = links().length > 0;
        find('#runSync').disabled = busy || !!problem;
    }
    async function run() {
        if (busy) return;
        let captured;
        try { captured = Object.freeze(buildSyncRequest(options(), links())); }
        catch (error) { common.status(find('#formStatus'), error.message, 'error'); return; }
        const name = users.find(user => user.Id === captured.userId)?.Name ?? captured.userId;
        const summary = `${captured.syncAction === 'UpdateJellyfin' ? 'Tracker → Jellyfin' : 'Jellyfin → Trackers'} · ${name}${captured.syncAction === 'UpdateJellyfin' ? ` · ${common.providerName(captured.provider)} · ${captured.status}` : ' · linked trackers'}`;
        busy = true; updateForm(); lastRun = { message: `Running: ${summary}`, tone: '' }; common.status(find('#runStatus'), lastRun.message);
        try {
            // No abort/cancellation promise: the server operation continues if this
            // view closes, and a failed connection cannot prove that it did not run.
            await common.request(`AniDoki/sync?${new URLSearchParams(captured)}`, { method: 'POST' });
            lastRun = { message: `${summary}. ${syncFeedback(true)}`, tone: 'success' };
        } catch { lastRun = { message: `${summary}. ${syncFeedback(false)}`, tone: 'error' }; }
        finally { busy = false; if (!lifetime.signal.aborted) { common.status(find('#runStatus'), lastRun.message, lastRun.tone); updateForm(); } }
    }
}
