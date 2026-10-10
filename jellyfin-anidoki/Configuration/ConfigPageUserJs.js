// Plugin Pages replaces its fetched fragment on each show. Preserve preferences
// against its cached host view, and dispose the previous fragment's handlers.
const cachedViews = new WeakMap();
export default function (view, host = view) {
    const cache = cachedViews.get(host) ?? { saved: null, baseline: null, draft: null, busy: false };
    cachedViews.set(host, cache); cache.lifecycle?.abort(); cache.active?.abort();
    cache.lifecycle = new AbortController();
    const root = view.querySelector('.anidoki'), find = selector => view.querySelector(selector);
    let common, state, validLibraries;
    const dirty = () => cache.baseline && (JSON.stringify(cache.baseline) !== JSON.stringify(cache.draft) || cache.libraryChoice?.mode === 'selected' && !cache.libraryChoice.selected.length);
    const updateDirty = () => { find('.ad-save-bar').hidden = !dirty(); if (dirty() && !cache.busy) common.status(find('#saveStatus'), 'Unsaved changes'); };
    const connected = () => cache.saved.ConnectedProviders ?? cache.saved.connectedProviders ?? [];
    const unsupportedRewatch = () => connected().length > 0 && connected().every(provider => ['Simkl', 'Annict'].includes(provider));
    function setBusy(value) {
        cache.busy = value;
        view.querySelectorAll('input, select, button').forEach(node => node.disabled = value);
        if (!value && cache.saved) find('#RewatchCompleted').disabled = unsupportedRewatch();
    }
    async function guard(action) {
        if (cache.busy) throw new Error('Wait for the current action to finish.'); setBusy(true);
        try { return await action(); } finally { setBusy(false); }
    }
    const show = () => {
        cache.active?.abort(); cache.active = new AbortController(); const signal = cache.active.signal;
        start(signal).catch(async error => {
            const message = common ? await common.errorMessage(error) : 'Could not load AniDōki resources.';
            if (!signal.aborted) find('[data-page-status]').textContent = message;
        });
    };
    host.addEventListener('viewshow', show, { signal: cache.lifecycle.signal });
    host.addEventListener('viewhide', () => cache.active?.abort(), { signal: cache.lifecycle.signal });
    show();

    async function start(signal) {
        [common, state] = await Promise.all([import(ApiClient.getUrl('AniDoki/assets/common.js')), import(ApiClient.getUrl('AniDoki/assets/config-state.js'))]);
        if (signal.aborted) return;
        common.ensureStyles(); common.prepareStickyShell(root, signal);
        const userId = ApiClient.getCurrentUserId();
        if (cache.userId && cache.userId !== userId) { cache.saved = null; cache.baseline = null; cache.draft = null; cache.libraryChoice = {}; }
        cache.userId = userId;
        if (!cache.saved) {
            const result = await Promise.all([common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal }), common.json('AniDoki/user/parameters', { signal })]);
            if (signal.aborted) return;
            [cache.saved, cache.parameters] = result; cache.baseline = state.userPreferences(cache.saved); cache.draft = state.userPreferences(cache.saved);
        }
        if (signal.aborted) return;
        find('#TemplateConfigForm').hidden = false; common.status(find('[data-page-status]'), ''); render(signal);
        for (const field of ['PlanToWatchOnly', 'RewatchCompleted']) find(`#${field}`).addEventListener('change', event => { cache.draft[field] = event.target.checked; updateDirty(); }, { signal });
        find('#TemplateConfigForm').addEventListener('submit', event => { event.preventDefault(); save(signal); }, { signal });
        find('#discardChanges').addEventListener('click', () => { cache.draft = state.userPreferences(cache.baseline); cache.libraryChoice = {}; render(signal); }, { signal });
        find('#refreshAccounts').addEventListener('click', async () => {
            common.refreshFeedback(find('#accountsRefreshStatus'), false); common.status(find('#accountsStatus'), '');
            try { await guard(async () => { cache.saved = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal }); if (!signal.aborted) { renderAccounts(signal); common.refreshFeedback(find('#accountsRefreshStatus'), true); } }); }
            catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted) common.status(find('#accountsStatus'), message, 'error');
            }
        }, { signal });
        common.observeSaveBar(root, signal); common.bindDraftWarning(view, dirty, signal); setBusy(cache.busy);
    }
    function render(signal) {
        for (const field of ['PlanToWatchOnly', 'RewatchCompleted']) find(`#${field}`).checked = cache.draft[field];
        renderAccounts(signal);
        cache.libraryChoice ??= {};
        validLibraries = common.renderLibraries(find('#libraries'), cache.draft, cache.parameters.libraries ?? [], signal, updateDirty, cache.libraryChoice);
        updateDirty();
    }
    function renderAccounts(signal) {
        const available = (cache.parameters.providerList ?? []).map(provider => provider.Key);
        const visible = [...new Set([...available, 'Kitsu', 'Annict', ...connected()])];
        common.renderAccounts(find('#linkedAccounts'), { linked: connected(), visible, userName: 'your account', action: (kind, provider, credentials) => accountAction(kind, provider, credentials, signal) }, signal);
        for (const provider of connected()) if (!available.includes(provider) && !['Kitsu', 'Annict'].includes(provider)) find('#linkedAccounts').append(common.element('p', 'ad-help', `${common.providerName(provider)} is linked, but the server’s app setup is incomplete. Ask your administrator to check it.`));
        find('#RewatchCompleted').disabled = cache.busy || unsupportedRewatch();
        find('#rewatchHelp').textContent = unsupportedRewatch() ? 'Your linked trackers (Simkl / Annict) do not support this option. Its saved value is preserved.' : 'Automatically treat completed titles as rewatches. Simkl and Annict do not support this option.';
    }
    async function accountAction(kind, provider, credentials, signal) {
        return guard(async () => {
            if (kind === 'test') {
                const profile = await common.json(`AniDoki/user/user?apiName=${provider}&user=${cache.userId}`, { signal });
                return { message: profile?.name || profile?.Name ? `Connection verified for ${profile.name ?? profile.Name}.` : 'Connection verified.' };
            }
            if (kind === 'link' && provider !== 'Kitsu') return { authorizationUrl: await common.json(`AniDoki/user/buildAuthorizeRequestUrl?provider=${provider}&user=${cache.userId}`, { signal }) };
            if (kind === 'disconnect') await common.request(`AniDoki/user/deauthenticate?apiName=${provider}&user=${cache.userId}`, { signal });
            else await common.request('AniDoki/user/passwordGrant', { method: 'POST', body: { Provider: provider, User: cache.userId, ...credentials }, signal });
            cache.saved = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal });
            const message = kind === 'disconnect' ? `${common.providerName(provider)} disconnected.` : 'Account linked. Your preference edits are retained.';
            if (!signal.aborted) { renderAccounts(signal); common.status(find('#accountsStatus'), message, 'success'); }
            return { message };
        });
    }
    async function save(signal) {
        if (cache.busy || !dirty()) return;
        try {
            if (!validLibraries()) throw new Error('Pick at least one library, or choose All libraries.');
            await guard(async () => {
                const latest = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal });
                const merged = state.mergePreferences(latest, cache.baseline, cache.draft);
                if (merged.conflicts.length) throw new Error(`Changed on the server: ${merged.conflicts.join(', ')}. Your edits are retained. Reload to review before saving.`);
                common.status(find('#saveStatus'), 'Saving…');
                const response = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { method: 'PUT', body: merged.value, signal });
                cache.saved = response; cache.baseline = state.userPreferences(response); cache.draft = state.userPreferences(response); cache.libraryChoice = {};
                if (!signal.aborted) { render(signal); common.status(find('[data-page-status]'), 'Preferences saved.', 'success'); }
            });
        } catch (error) {
            const message = await common.errorMessage(error);
            if (!signal.aborted) common.status(find('#saveStatus'), message, 'error');
        }
    }
}
