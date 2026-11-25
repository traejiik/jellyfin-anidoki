// Plugin Pages replaces its fetched fragment on each show. Preserve preferences
// against its cached host view, and dispose the previous fragment's handlers.
const cachedViews = new WeakMap();
// Same URL as common.assetUrl, which cannot be used before common.js loads.
const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/v/${globalThis.anidokiAssetVersion ??= Date.now().toString(36)}/${name}`);

export function openMatchesFromSettingsLink(view, requestKey, openMatches, win = window) {
    const url = new URL(win.location.href);
    const [route, query] = url.hash.split('?');
    const params = new URLSearchParams(query);
    const hasMarker = params.get('dokiSection') === 'trackerMatchesTitle';
    let request = null;
    try {
        const session = win.sessionStorage;
        const pending = session?.getItem(requestKey);
        session?.removeItem(requestKey);
        request = JSON.parse(pending ?? 'null');
    } catch {
        // Direct links carry the marker in the route.
    }
    const sourceKey = (hasMarker && params.get('dokiSource')) ||
        (request?.expires > Date.now() && typeof request.sourceKey === 'string' ? request.sourceKey : null);
    if (!hasMarker && !sourceKey)
        return;
    if (hasMarker) {
        params.delete('dokiSection');
        params.delete('dokiSource');
        url.hash = `${route}?${params}`;
        win.history.replaceState(win.history.state ?? null, '', url.href);
    }
    view.querySelector('[data-section="#doki-advanced"]')?.click();
    return openMatches(sourceKey || null);
}

export default function (view, host = view) {
    const cache = cachedViews.get(host) ?? { saved: null, baseline: null, draft: null, busy: false };
    cachedViews.set(host, cache); cache.lifecycle?.abort(); cache.active?.abort();
    cache.lifecycle = new AbortController();
    const root = view.querySelector('.anidoki'), find = selector => view.querySelector(selector);
    let common, state, history;
    const preferencesDirty = () => cache.baseline && JSON.stringify(cache.baseline) !== JSON.stringify(cache.draft);
    // Discord saves with the page, so its edits count as unsaved changes too.
    const dirty = () => !!(preferencesDirty() || cache.discordDirty?.());
    const updateDirty = () => { find('.doki-save-bar').hidden = !dirty(); if (dirty() && !cache.busy) common.status(find('#saveStatus'), 'Unsaved changes'); };
    const connected = () => cache.saved.ConnectedProviders ?? cache.saved.connectedProviders ?? [];
    const unsupportedRewatch = () => connected().length > 0 && connected().every(provider => ['Simkl', 'Annict'].includes(provider));
    function setBusy(value) {
        cache.busy = value;
        view.querySelectorAll('input, select, button').forEach(node => node.disabled = value);
        if (!value && cache.saved) find('#RewatchCompleted').disabled = unsupportedRewatch();
        // Re-apply Discord's own disabled rules (Send test needs a saved webhook) after the page-wide unlock.
        if (!value) cache.discordRefresh?.();
        // The Matches section's Save, Discard and pager have rules of their own too.
        if (!value) cache.matchesRefresh?.();
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
        [common, state, history] = await Promise.all([import(assetUrl('common.js')), import(assetUrl('config-state.js')), import(assetUrl('history-state.js'))]);
        if (signal.aborted) return;
        common.ensureStyles(); common.prepareStickyShell(root, signal);
        common.followThemeAccent(root, signal);
        const userId = ApiClient.getCurrentUserId();
        if (cache.userId && cache.userId !== userId) { cache.activity = null; cache.saved = null; cache.baseline = null; cache.draft = null; cache.discordDraft = null; cache.discordDirty = null; cache.matches = null; cache.matchesDirty = null; }
        cache.userId = userId;
        if (!cache.saved) {
            const result = await Promise.all([common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal }), common.json('AniDoki/user/parameters', { signal })]);
            if (signal.aborted) return;
            [cache.saved, cache.parameters] = result; cache.baseline = state.userPreferences(cache.saved); cache.draft = state.userPreferences(cache.saved);
        }
        if (signal.aborted) return;
        find('#TemplateConfigForm').hidden = false; common.status(find('[data-page-status]'), ''); render(signal);
        loadActivity(signal);
        for (const field of ['PlanToWatchOnly', 'AddUnlistedAnime', 'RewatchCompleted', 'ShowLogNotifications']) find(`#${field}`).addEventListener('change', event => { cache.draft[field] = event.target.checked; nestUnlisted(); updateDirty(); }, { signal });
        find('#TemplateConfigForm').addEventListener('submit', event => { event.preventDefault(); save(signal); }, { signal });
        find('#discardChanges').addEventListener('click', () => { cache.draft = state.userPreferences(cache.baseline); cache.discordDiscard?.(); render(signal); }, { signal });
        find('#refreshAccounts').addEventListener('click', async () => {
            common.refreshFeedback(find('#accountsRefreshStatus'), false); common.status(find('#accountsStatus'), '');
            try { await guard(async () => { cache.saved = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal }); if (!signal.aborted) { renderAccounts(signal); common.refreshFeedback(find('#accountsRefreshStatus'), true); loadActivity(signal); } }); }
            catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted) common.status(find('#accountsStatus'), message, 'error');
            }
        }, { signal });
        // History loads on its own, so a failure there leaves the rest of the page working.
        import(common.assetUrl('history.js')).then(table => {
            if (!signal.aborted) table.default(find('#historyTable'), { load: (offset, limit) => common.json(`AniDoki/user/history?user=${cache.userId}&offset=${offset}&limit=${limit}`, { signal }) }, common, signal);
        }).catch(async error => {
            if (signal.aborted) return;
            const line = common.element('p', 'doki-status'); common.status(line, await common.errorMessage(error), 'error'); find('#historyTable').replaceChildren(line);
        });
        common.observeSaveBar(root, signal); common.bindNavigation(root, signal); common.bindDraftWarning(view, () => dirty() || !!cache.matchesDirty?.(), signal); setBusy(cache.busy);
        const discord = await import(common.assetUrl('discord-settings.js'));
        if (!signal.aborted) await discord.default(root, common, cache, signal, () => cache.busy, updateDirty);
        if (!signal.aborted) updateDirty();
        // A match draft warns on leaving (above) but not through the Save changes bar, which would not save it.
        // A failure here leaves the rest of the page working and says so in the section.
        if (!signal.aborted) await import(common.assetUrl('user-matches.js'))
            .then(async matches => {
                if (signal.aborted) return;
                const openMatches = await matches.default(find('#doki-advanced'), common, cache, signal);
                if (!signal.aborted)
                    await openMatchesFromSettingsLink(view, common.matchesSettingsRequest, openMatches);
            })
            .catch(async error => { if (!signal.aborted) common.status(find('#matchesStatus'), await common.errorMessage(error), 'error'); });
    }
    function render(signal) {
        for (const field of ['PlanToWatchOnly', 'AddUnlistedAnime', 'RewatchCompleted', 'ShowLogNotifications']) find(`#${field}`).checked = cache.draft[field];
        renderThreshold(signal);
        nestUnlisted();
        renderAccounts(signal);
        updateDirty();
    }
    // Adding unlisted anime only applies with Plan to watch only on, so it is hidden otherwise; its value is kept.
    function nestUnlisted() { find('#addUnlistedRow').hidden = !cache.draft.PlanToWatchOnly; }
    // Re-rendered with the page, so Discard and a save show the stored choice again.
    function renderThreshold(signal) {
        const maxResumePct = cache.parameters?.maxResumePct ?? 90, value = cache.draft.TrackerThresholdPercent;
        find('#thresholdSetting').replaceChildren(common.thresholdSetting({ value, maxResumePct, choices: state.thresholdChoices(maxResumePct),
            serverDefault: cache.parameters?.defaultThresholdPercent ?? null,
            fallsBack: state.thresholdFallsBack(value, maxResumePct), signal,
            onChange: percent => { cache.draft.TrackerThresholdPercent = percent; updateDirty(); } }));
        if (cache.busy) find('#thresholdSetting select').disabled = true;
    }
    function renderAccounts(signal) {
        const available = (cache.parameters.providerList ?? []).map(provider => provider.Key);
        const visible = [...new Set([...available, 'Kitsu', 'Annict', ...connected()])];
        common.renderAccounts(find('#linkedAccounts'), { linked: connected(), visible, userName: 'your account', summaries: summaries(), action: (kind, provider, credentials) => accountAction(kind, provider, credentials, signal) }, signal);
        for (const provider of connected()) if (!available.includes(provider) && !['Kitsu', 'Annict'].includes(provider)) find('#linkedAccounts').append(common.element('p', 'doki-help', `${common.providerName(provider)} is linked, but the server’s app setup is incomplete. Ask your administrator to check it.`));
        find('#RewatchCompleted').disabled = cache.busy || unsupportedRewatch();
        find('#rewatchHelp').textContent = unsupportedRewatch() ? 'Your linked trackers (Simkl / Annict) do not support this option. Its saved value is preserved.' : 'Automatically treat completed titles as rewatches. Simkl and Annict do not support this option.';
    }
    // Last update and attention per linked tracker. Without them (still loading, or the read failed) the rows show neither.
    function summaries() {
        if (!cache.activity) return {};
        return Object.fromEntries(connected().map(provider => {
            const tracker = cache.activity.trackers?.find(item => item.provider === provider);
            const attention = tracker?.attention;
            return [provider, {
                lastUpdate: history.lastUpdateLine(tracker?.lastUpdateUtc),
                attention: history.attentionText(provider, attention, { surface: 'user' }),
                signInAgain: attention?.kind === 'SignInRejected'
            }];
        }));
    }
    async function loadActivity(signal) {
        try {
            const activity = await common.json(`AniDoki/user/activity?user=${cache.userId}`, { signal });
            if (signal.aborted) return;
            cache.activity = activity; renderAccounts(signal);
        } catch { /* The rows work without it; History reports a storage or connection problem itself. */ }
    }
    async function accountAction(kind, provider, credentials, signal) {
        return guard(async () => {
            if (kind === 'test') {
                const profile = await common.json(`AniDoki/user/user?apiName=${provider}&user=${cache.userId}`, { signal });
                return { message: profile?.name || profile?.Name ? `Connection verified for ${profile.name ?? profile.Name}.` : 'Connection verified.' };
            }
            if (kind === 'link' && provider !== 'Kitsu') return { authorizationUrl: await common.json(`AniDoki/user/buildAuthorizeRequestUrl?provider=${provider}&user=${cache.userId}`, { signal }) };
            if (kind === 'disconnect') await common.request(`AniDoki/user/deauthenticate?apiName=${provider}&user=${cache.userId}`, { method: 'POST', signal });
            else await common.request('AniDoki/user/passwordGrant', { method: 'POST', body: { Provider: provider, User: cache.userId, ...credentials }, signal });
            cache.saved = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal });
            const message = kind === 'disconnect' ? `${common.providerName(provider)} disconnected.` : 'Account linked. Your preference edits are retained.';
            if (!signal.aborted) { renderAccounts(signal); common.status(find('#accountsStatus'), message, 'success'); loadActivity(signal); }
            return { message };
        });
    }
    async function save(signal) {
        if (cache.busy || !dirty()) return;
        try {
            await guard(async () => {
                common.status(find('#saveStatus'), 'Saving…');
                if (preferencesDirty()) {
                    const latest = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { signal });
                    const merged = state.mergePreferences(latest, cache.baseline, cache.draft);
                    if (merged.conflicts.length) throw new Error(`Changed on the server: ${merged.conflicts.join(', ')}. Your edits are retained. Reload to review before saving.`);
                    const response = await common.json(`AniDoki/user/configuration?user=${cache.userId}`, { method: 'PUT', body: merged.value, signal });
                    cache.saved = response; cache.baseline = state.userPreferences(response); cache.draft = state.userPreferences(response);
                    if (!signal.aborted) render(signal);
                }
                if (cache.discordDirty?.()) await cache.discordSave(signal);
                if (!signal.aborted) { updateDirty(); common.status(find('[data-page-status]'), 'Preferences saved.', 'success'); }
            });
        } catch (error) {
            const message = await common.errorMessage(error);
            if (!signal.aborted) common.status(find('#saveStatus'), message, 'error');
        }
    }
}
