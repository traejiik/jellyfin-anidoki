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
const units = { SyncToTrackers: 'seasons', SyncFromTrackers: 'tracker entries' };
const headings = {
    Succeeded: 'Finished', Partial: 'Finished with some problems', NoChange: 'Finished; nothing needed changing',
    Skipped: 'Finished; your tracking rules skipped these', Unresolved: 'Finished; some series could not be matched',
    Unconfirmed: 'Finished; some updates could not be confirmed', Failed: 'Failed', Cancelled: 'Cancelled',
    Interrupted: 'Interrupted when Jellyfin stopped'
};
const countLabels = [['Confirmed', 'updated'], ['NoChange', 'unchanged'], ['Skipped', 'skipped'], ['Unresolved', 'not matched'],
    ['Unconfirmed', 'not confirmed'], ['Failed', 'failed']];
export const isFinished = state => !['Queued', 'Running'].includes(state);
// Turns an operation view from AniDoki/operations/{id} into one status line and a tone.
export function describeOperation(view) {
    if (view.state === 'Queued') return { message: 'Waiting to start…', tone: '' };
    if (view.state === 'Running') {
        const message = view.total == null ? 'Running…' : `Running: ${view.done} of ${view.total} ${units[view.kind] ?? 'items'} checked…`;
        return { message, tone: '' };
    }
    const counts = countLabels.filter(([key]) => view.counts?.[key]).map(([key, label]) => `${view.counts[key]} ${label}`).join(' · ');
    const tone = ['Succeeded', 'NoChange'].includes(view.state) ? 'success' : ['Failed', 'Unconfirmed', 'Interrupted'].includes(view.state) ? 'error' : '';
    return { message: [headings[view.state] ?? view.state, counts, view.message].filter(Boolean).join('. '), tone };
}

// Same URL as common.assetUrl, which cannot be used before common.js loads.
const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/v/${globalThis.anidokiAssetVersion ??= Date.now().toString(36)}/${name}`);

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
        start(signal).catch(async error => {
            const message = common ? await common.errorMessage(error) : 'Could not load AniDōki resources.';
            if (!signal.aborted) find('[data-page-status]').textContent = message;
        });
    });
    async function start(signal) {
        common = await import(assetUrl('common.js')); if (signal.aborted) return;
        common.ensureStyles(); common.setTabs(common.TabManualSync, common.getTabs, view);
        common.selectPluginSidebar('AniDoki_ManualSync', signal, view.ownerDocument);
        find('#runSync').disabled = true; common.status(find('[data-page-status]'), 'Loading users and linked trackers…');
        const result = await Promise.all([ApiClient.getUsers(), ApiClient.getPluginConfiguration(common.pluginId)]); if (signal.aborted) return;
        [users, config] = result;
        find('#configurationSummary').replaceChildren(common.linkedUsersBadge(users, config.UserConfig));
        const selected = find('#selectSyncUser').value; common.populateUserList(view, users, '#selectSyncUser');
        if (users.some(user => user.Id === selected)) find('#selectSyncUser').value = selected;
        find('#ManualSyncConfigForm').hidden = false;
        common.status(find('[data-page-status]'), ''); updateProviders(); updateForm();
        busy = false;
        if (lastRun) {
            show(lastRun);
            if (!isFinished(lastRun.state ?? 'Queued')) follow(signal);
        }
        find('#cancelSync').addEventListener('click', async () => {
            const run = lastRun;
            if (!run?.operationId) return;
            find('#cancelSync').disabled = true;
            try {
                await common.request(`AniDoki/operations/${run.operationId}/cancel`, { method: 'POST' });
                Object.assign(run, { message: 'Cancelling after the current item…', tone: '' });
                show(run);
            } catch (error) {
                // 409: it finished first; the next poll shows the result.
                if (!(error instanceof Response && error.status === 409)) {
                    Object.assign(run, { message: `Could not cancel: ${await common.errorMessage(error)}`, tone: 'error' });
                    show(run);
                }
            } finally {
                find('#cancelSync').disabled = false;
            }
        }, { signal });
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
    function show(run) {
        common.status(find('#runStatus'), `${run.summary}: ${run.message}`, run.tone);
        find('#cancelSync').hidden = !run.operationId || isFinished(run.state ?? 'Queued');
    }
    // Polls until the run finishes. Leaving the page stops polling, not the run; coming back resumes it.
    async function follow(signal) {
        const run = lastRun;
        if (!run?.operationId) return;
        busy = true;
        updateForm();
        while (!signal.aborted) {
            try {
                const view = await common.json(`AniDoki/operations/${run.operationId}`, { signal });
                if (signal.aborted) return;
                Object.assign(run, describeOperation(view), { state: view.state });
                show(run);
                if (isFinished(view.state)) break;
            } catch (error) {
                if (signal.aborted) return;
                Object.assign(run, { message: `Could not read progress: ${await common.errorMessage(error)}`, tone: 'error' });
                show(run);
            }
            await new Promise(resolve => setTimeout(resolve, 2000));
        }
        if (signal.aborted) return;
        busy = false;
        updateForm();
    }
    async function run() {
        if (busy) return;
        let captured;
        try { captured = Object.freeze(buildSyncRequest(options(), links())); }
        catch (error) { common.status(find('#formStatus'), error.message, 'error'); return; }
        const name = users.find(user => user.Id === captured.userId)?.Name ?? captured.userId;
        const summary = `${captured.syncAction === 'UpdateJellyfin' ? 'Tracker → Jellyfin' : 'Jellyfin → Trackers'} · ${name}${captured.syncAction === 'UpdateJellyfin' ? ` · ${common.providerName(captured.provider)} · ${captured.status}` : ' · linked trackers'}`;
        busy = true;
        updateForm();
        lastRun = { summary, message: 'Starting…', tone: '' };
        show(lastRun);
        let started;
        try {
            started = await common.json(`AniDoki/sync?${new URLSearchParams(captured)}`, { method: 'POST' });
        } catch (error) {
            // A run already queued or running for this user and direction answers 409 with its ID; follow that one.
            if (error instanceof Response && error.status === 409) {
                started = await error.json();
                lastRun.summary = `${summary} (already running)`;
            } else {
                busy = false;
                Object.assign(lastRun, { message: await common.errorMessage(error), tone: 'error' });
                if (!lifetime.signal.aborted) { show(lastRun); updateForm(); }
                return;
            }
        }
        lastRun.operationId = started.operationId;
        follow(lifetime.signal);
    }
}
