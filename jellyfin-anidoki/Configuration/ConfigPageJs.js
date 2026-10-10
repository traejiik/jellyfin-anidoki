export default function (view) {
    let lifetime, saved, baseline, draft, users, parameters, selectedUser, busy = false;
    let common, state, previewGeneration = 0, previewTimer;
    const libraryValidators = new Map();
    const libraryChoices = new Map();
    const root = view.querySelector('.anidoki');
    const find = selector => view.querySelector(selector);
    const connected = userId => (saved.UserConfig?.find(user => user.UserId === userId)?.UserApiAuth ?? []).map(auth => auth.Name);
    const dirty = () => baseline && (state.changedValues(baseline, draft).length > 0 || [...libraryChoices.values()].some(choice => choice.mode === 'selected' && !choice.selected.length));
    const updateDirty = () => {
        const changed = dirty(); find('.ad-save-bar').hidden = !changed;
        if (changed && !busy) common.status(find('#saveStatus'), 'Unsaved changes');
    };
    const setBusy = value => {
        busy = value;
        view.querySelectorAll('button, input, select').forEach(node => node.disabled = value);
        if (!value) find('#copyCallback').disabled = !find('#generalCallbackUrlInput').value;
    };
    const guard = async action => {
        if (busy) throw new Error('Wait for the current action to finish.');
        setBusy(true);
        try { return await action(); } finally { setBusy(false); }
    };

    view.addEventListener('viewhide', () => { lifetime?.abort(); clearTimeout(previewTimer); });
    view.addEventListener('viewshow', () => {
        lifetime?.abort(); lifetime = new AbortController();
        const signal = lifetime.signal;
        start(signal).catch(async error => {
            const message = common ? await common.errorMessage(error) : 'Could not load AniDōki resources. Reload to try again.';
            if (!signal.aborted) find('[data-page-status]').textContent = message;
        });
    });

    async function start(signal) {
        [common, state] = await Promise.all([
            import(ApiClient.getUrl('AniDoki/assets/common.js')),
            import(ApiClient.getUrl('AniDoki/assets/config-state.js'))
        ]);
        if (signal.aborted) return;
        common.ensureStyles(); common.prepareStickyShell(root, signal); common.setTabs(common.TabGeneral, common.getTabs, view);
        common.status(find('[data-page-status]'), 'Loading settings…');
        if (!saved) {
            const result = await Promise.all([ApiClient.getPluginConfiguration(common.pluginId), ApiClient.getUsers(), common.json('AniDoki/parameters', { signal })]);
            if (signal.aborted) return;
            [saved, users, parameters] = result;
            const editable = structuredClone(saved);
            editable.ProviderApiAuth ??= [];
            for (const provider of common.providers) if (!editable.ProviderApiAuth.some(item => item.Name === provider.key)) editable.ProviderApiAuth.push({ Name: provider.key, ClientId: '', ClientSecret: '' });
            editable.UserConfig ??= [];
            for (const user of users) if (!editable.UserConfig.some(item => item.UserId === user.Id)) editable.UserConfig.push({ UserId: user.Id, ...state.userPreferences() });
            baseline = state.createDraft(editable); draft = state.createDraft(baseline);
        }
        if (signal.aborted) return;
        find('#TemplateConfigForm').hidden = false; common.status(find('[data-page-status]'), '');
        render(signal);
        root.addEventListener('input', event => {
            const field = event.target.dataset.field;
            if (field) {
                draft[field] = event.target.type === 'checkbox' ? event.target.checked : event.target.type === 'number' ? Number(event.target.value) : event.target.value;
                if (field === 'callbackUrl') schedulePreview(signal);
                updateDirty();
            }
        }, { signal });
        find('#TemplateConfigForm').addEventListener('submit', event => { event.preventDefault(); save(signal); }, { signal });
        find('#discardChanges').addEventListener('click', () => { draft = state.createDraft(baseline); libraryValidators.clear(); libraryChoices.clear(); render(signal); }, { signal });
        find('#closeUserPanel').addEventListener('click', () => {
            const userId = selectedUser;
            selectedUser = undefined; find('#userPanel').hidden = true;
            const button = [...find('#userRows').querySelectorAll('button')].find(button => button.dataset.userId === userId);
            button?.setAttribute('aria-expanded', 'false'); button?.focus();
        }, { signal });
        find('#refreshAccounts').addEventListener('click', async () => {
            common.refreshFeedback(find('#accountsRefreshStatus'), false); common.status(find('#accountsStatus'), '');
            try {
                await guard(async () => {
                    saved = await ApiClient.getPluginConfiguration(common.pluginId);
                    if (!signal.aborted) { renderUsers(signal); if (selectedUser) renderUser(signal); common.refreshFeedback(find('#accountsRefreshStatus'), true); }
                });
            } catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted) common.status(find('#accountsStatus'), message, 'error');
            }
        }, { signal });
        find('#useCurrent').addEventListener('click', () => fillAddress(ApiClient.serverAddress(), signal), { signal });
        find('#useLocal').addEventListener('click', async () => {
            const generation = ++previewGeneration;
            try { const result = await common.json('AniDoki/callbackPreview', { signal }); if (!signal.aborted && generation === previewGeneration) fillAddress(result.baseAddress, signal); }
            catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted && generation === previewGeneration) common.status(find('#addressStatus'), message, 'error');
            }
        }, { signal });
        find('#copyCallback').addEventListener('click', async () => {
            try {
                await navigator.clipboard.writeText(find('#generalCallbackUrlInput').value);
                if (!signal.aborted) common.status(find('#addressStatus'), 'Callback copied.', 'success');
            } catch {
                if (!signal.aborted) common.status(find('#addressStatus'), 'Could not copy. Select the callback and copy it manually.', 'error');
            }
        }, { signal });
        find('#testAnimeListSaveLocation').addEventListener('click', async () => {
            const location = draft.animeListSaveLocation ?? '';
            if (!location.trim()) { common.status(find('#folderStatus'), 'Enter a folder first.', 'error'); return; }
            common.status(find('#folderStatus'), 'Testing folder…');
            try {
                await guard(async () => {
                    const result = await common.json(`AniDoki/testAnimeListSaveLocation?saveLocation=${encodeURIComponent(location)}`, { signal });
                    if (!signal.aborted) common.status(find('#folderStatus'), result || 'Folder is writable. Save changes to use it.', result ? 'error' : 'success');
                });
            } catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted) common.status(find('#folderStatus'), message, 'error');
            }
        }, { signal });
        common.bindNavigation(root, signal); common.observeSaveBar(root, signal); common.bindDraftWarning(view, dirty, signal);
        setBusy(busy);
    }

    function fillAddress(address, signal) {
        draft.callbackUrl = address; find('#apiUrl').value = address; updateDirty(); schedulePreview(signal);
    }
    function schedulePreview(signal) {
        clearTimeout(previewTimer); const generation = ++previewGeneration;
        find('#generalCallbackUrlInput').value = ''; find('#copyCallback').disabled = true; find('#addressCheck').hidden = true;
        previewTimer = setTimeout(async () => {
            try {
                const result = await common.json(`AniDoki/callbackPreview?address=${encodeURIComponent(draft.callbackUrl ?? '')}`, { signal });
                if (signal.aborted || generation !== previewGeneration) return;
                find('#apiUrl').setAttribute('aria-invalid', 'false');
                find('#generalCallbackUrlInput').value = result.callbackUrl;
                find('#copyCallback').disabled = busy;
                const marker = new URL(common.safeExternalUrl(result.baseAddress)); marker.pathname = marker.pathname.replace(/\/$/, '') + '/AniDoki/apiUrlTest';
                find('#addressCheck').href = marker.href; find('#addressCheck').hidden = false;
                common.status(find('#addressStatus'), 'Callback preview ready.');
            } catch (error) {
                const message = await common.errorMessage(error);
                if (!signal.aborted && generation === previewGeneration) {
                    find('#apiUrl').setAttribute('aria-invalid', 'true');
                    common.status(find('#addressStatus'), message, 'error');
                }
            }
        }, 250);
        signal.addEventListener('abort', () => clearTimeout(previewTimer), { once: true });
    }

    function render(signal) {
        view.querySelectorAll('[data-field]').forEach(input => {
            const value = draft[input.dataset.field];
            if (input.type === 'checkbox') input.checked = value ?? false;
            else input.value = input.dataset.field === 'authenticationLinkExpireTimeMinutes' ? value || 1440 : value ?? '';
        });
        renderProviders(signal); renderUsers(signal); if (selectedUser) renderUser(signal);
        updateDirty(); schedulePreview(signal);
    }
    function renderProviders(signal) {
        const container = find('#providerCards'); container.replaceChildren();
        for (const provider of common.providers) {
            const record = draft.ProviderApiAuth.find(item => item.Name === provider.key);
            const savedRecord = baseline.ProviderApiAuth.find(item => item.Name === provider.key);
            const row = common.element('details', 'ad-provider');
            const summary = common.element('summary');
            const badge = common.element('span', 'ad-badge');
            const refreshBadge = () => {
                const unsaved = ['ClientId', 'ClientSecret'].some(field => record[field] !== savedRecord?.[field]) || provider.key === 'Shikimori' && ['shikimoriAppName', 'shikimoriDomain'].some(field => draft[field] !== baseline[field]);
                const readiness = provider.key === 'Kitsu' ? 'Per-user sign-in' : provider.key === 'Annict' ? 'Personal token under Users' : !record.ClientId ? 'Missing client ID' : !record.ClientSecret ? 'Missing secret' : provider.key === 'Shikimori' && !draft.shikimoriAppName?.trim() ? 'Missing app name' : 'Configured';
                badge.textContent = unsaved ? `${readiness} · Unsaved` : readiness;
            };
            refreshBadge();
            summary.append(common.providerIcon(provider), common.element('span', 'ad-provider-title', provider.name), badge); row.append(summary);
            const body = common.element('div', 'ad-provider-body');
            if (['Kitsu', 'Annict'].includes(provider.key)) {
                body.append(common.element('p', 'ad-help', provider.key === 'Kitsu' ? 'Kitsu does not need a server-wide OAuth app. Select a user below to sign in with their username and password.' : 'Annict uses a personal access token. Select the intended user below to link it; tokens are never copied to another user.'));
            } else {
                const grid = common.element('div', 'ad-grid');
                for (const [field, title, type] of [['ClientId', 'Client ID', 'text'], ['ClientSecret', 'Client secret', 'password']]) {
                    const fieldRoot = common.element('div', 'ad-field'); const label = common.element('label', '', title);
                    const input = common.element('input', 'ad-input ad-mono'); input.type = type; input.value = record[field]; input.autocomplete = 'off'; input.id = `ad-${provider.key}-${field}`; label.htmlFor = input.id;
                    let clear;
                    input.addEventListener('input', () => { record[field] = input.value; if (clear) clear.hidden = !input.value; refreshBadge(); updateDirty(); }, { signal });
                    fieldRoot.append(label, type === 'password' ? common.secretInput(input, provider.name, signal) : input);
                    if (type === 'password') {
                        const footer = common.element('div', 'ad-field-footer');
                        clear = common.element('button', 'ad-button ad-button-quiet', 'Clear secret'); clear.type = 'button'; clear.hidden = !input.value; clear.setAttribute('aria-label', `Clear ${provider.name} secret`);
                        const confirmation = common.element('div', 'ad-clear-confirm'); confirmation.hidden = true; confirmation.id = `ad-${provider.key}-clear-confirm`;
                        clear.setAttribute('aria-controls', confirmation.id); clear.setAttribute('aria-expanded', 'false');
                        confirmation.append(common.element('p', 'ad-help', `Clear the ${provider.name} secret when you save changes?`));
                        const actions = common.element('div', 'ad-actions');
                        const confirm = common.element('button', 'ad-button', 'Confirm clear'); confirm.type = 'button'; confirm.setAttribute('aria-label', `Confirm clear ${provider.name} secret`);
                        confirm.addEventListener('click', () => { input.value = ''; record[field] = ''; clear.hidden = true; confirmation.hidden = true; clear.setAttribute('aria-expanded', 'false'); refreshBadge(); updateDirty(); input.focus(); }, { signal });
                        const keep = common.element('button', 'ad-button', 'Keep secret'); keep.type = 'button'; keep.addEventListener('click', () => { confirmation.hidden = true; clear.setAttribute('aria-expanded', 'false'); clear.focus(); }, { signal });
                        actions.append(confirm, keep); confirmation.append(actions);
                        clear.addEventListener('click', () => { confirmation.hidden = false; clear.setAttribute('aria-expanded', 'true'); keep.focus(); }, { signal });
                        footer.append(clear); fieldRoot.append(footer, confirmation);
                    }
                    grid.append(fieldRoot);
                }
                body.append(grid);
                if (provider.key === 'Shikimori') {
                    for (const [field, title] of [['shikimoriAppName', 'App name (User-Agent)'], ['shikimoriDomain', 'Shikimori domain']]) {
                        const label = common.element('label', 'ad-field', title); const input = common.element('input', 'ad-input'); input.dataset.field = field; input.value = draft[field] ?? ''; label.append(input); body.append(label);
                        input.addEventListener('input', () => { draft[field] = input.value; refreshBadge(); }, { signal });
                    }
                }
                if (provider.key === 'Simkl') {
                    const label = common.element('label', 'ad-check'); const input = common.element('input', 'ad-switch'); input.type = 'checkbox'; input.setAttribute('role', 'switch'); input.dataset.field = 'simklUpdateAll'; input.checked = draft.simklUpdateAll ?? false; label.append(input, common.element('span', '', 'Update all Simkl episodes up to the current point')); body.append(label);
                }
            }
            row.append(body); container.append(row);
            row.addEventListener('toggle', () => { if (row.open) container.querySelectorAll('details').forEach(item => { if (item !== row) item.open = false; }); }, { signal });
        }
    }
    function renderUsers(signal) {
        find('#userRows').replaceChildren();
        const count = users.filter(user => connected(user.Id).length).length;
        find('#configurationSummary').replaceChildren(common.element('span', 'ad-badge', `${count} ${count === 1 ? 'user' : 'users'} linked`));
        if (!users.length) { const row = common.element('tr'); const cell = common.element('td', 'ad-help', 'No Jellyfin users found.'); cell.colSpan = 3; row.append(cell); find('#userRows').append(row); }
        for (const user of users) {
            const row = common.element('tr'); const title = common.element('td'); title.append(common.element('strong', '', user.Name));
            const linked = common.element('td', 'ad-muted', connected(user.Id).map(common.providerName).join(', ') || 'No linked accounts');
            const actions = common.element('td');
            const button = common.element('button', 'ad-button ad-icon-button'); button.append(common.icon('pencil')); button.type = 'button'; button.dataset.userId = user.Id; button.title = `Edit ${user.Name} tracking settings`; button.setAttribute('aria-label', `Manage ${user.Name}`); button.setAttribute('aria-controls', 'userPanel'); button.setAttribute('aria-expanded', String(selectedUser === user.Id));
            button.addEventListener('click', () => { selectedUser = user.Id; renderUser(signal); find('#userPanel').scrollIntoView({ block: 'start' }); find('#selectedUserName').focus({ preventScroll: true }); }, { signal });
            actions.append(button); row.append(title, linked, actions); find('#userRows').append(row);
        }
    }
    function renderUser(signal) {
        const user = users.find(item => item.Id === selectedUser); if (!user) return;
        const prefs = draft.UserConfig.find(item => item.UserId === selectedUser);
        find('#userPanel').hidden = false; find('#selectedUserName').textContent = user.Name;
        find('#userRows').querySelectorAll('button').forEach(button => button.setAttribute('aria-expanded', String(button.dataset.userId === selectedUser)));
        common.renderAccounts(find('#userAccounts'), { linked: connected(user.Id), visible: common.providers.map(item => item.key), admin: true, userName: user.Name,
            action: (kind, provider, credentials) => accountAction(user.Id, kind, provider, credentials, signal) }, signal);
        find('#userPreferences').replaceChildren();
        for (const [field, title, help] of [['PlanToWatchOnly', 'Only update anime on Plan to watch', 'Limit changes to titles already on the user’s plan-to-watch list.'], ['RewatchCompleted', 'Rewatch completed anime', 'Automatically treat completed titles as rewatches. Simkl and Annict do not support this option.']]) {
            const label = common.element('label', 'ad-check'); const input = common.element('input', 'ad-switch'); input.type = 'checkbox'; input.setAttribute('role', 'switch'); input.checked = prefs[field];
            input.addEventListener('change', () => { prefs[field] = input.checked; updateDirty(); }, { signal });
            const text = common.element('span'); text.append(common.element('strong', '', title), common.element('span', 'ad-help', help)); label.append(input, text); find('#userPreferences').append(label);
        }
        if (!libraryChoices.has(user.Id)) libraryChoices.set(user.Id, {});
        libraryValidators.set(user.Id, common.renderLibraries(find('#userLibraries'), prefs, parameters.libraries ?? [], signal, updateDirty, libraryChoices.get(user.Id)));
    }
    async function accountAction(userId, kind, provider, credentials, signal) {
        return guard(async () => {
            let response;
            if (kind === 'test') {
                response = await common.json(`AniDoki/user?apiName=${provider}&user=${userId}`, { signal });
                return { message: response?.name || response?.Name ? `Connection verified for ${response.name ?? response.Name}.` : 'Connection verified.' };
            }
            if (kind === 'disconnect') await common.request(`AniDoki/deauthenticate?apiName=${provider}&user=${userId}`, { signal });
            else if (provider === 'Kitsu') await common.request('AniDoki/passwordGrant', { method: 'POST', body: { Provider: provider, User: userId, ...credentials }, signal });
            else if (provider === 'Annict') await common.request(`AniDoki/annictToken?user=${userId}`, { method: 'POST', body: credentials, signal });
            else {
                const changed = state.changedValues(baseline, draft).some(change => change.group === 'provider' && change.id === provider || provider === 'Shikimori' && ['shikimoriAppName', 'shikimoriDomain'].includes(change.field) || change.field === 'callbackUrl');
                if (changed) throw new Error('Save the provider app and callback address changes before linking. Other edits are not saved by Link.');
                response = await common.json(`AniDoki/authorize?provider=${provider}&user=${userId}`, { signal });
                return { authorizationUrl: response };
            }
            saved = await ApiClient.getPluginConfiguration(common.pluginId);
            const message = kind === 'disconnect' ? `${common.providerName(provider)} disconnected.` : 'Account linked. Preference drafts are still unsaved.';
            if (!signal.aborted) { renderUsers(signal); renderUser(signal); common.status(find('#accountsStatus'), message, 'success'); }
            return { message };
        });
    }
    async function save(signal) {
        if (busy || !dirty()) return;
        try {
            for (const [id, valid] of libraryValidators) if (!valid()) throw new Error(`Pick at least one library for ${users.find(user => user.Id === id)?.Name}, or choose All libraries.`);
            const expiry = draft.authenticationLinkExpireTimeMinutes;
            if (expiry !== baseline.authenticationLinkExpireTimeMinutes && (!Number.isSafeInteger(expiry) || expiry < 1)) throw new Error('Authentication expiry must be a positive whole number of minutes.');
            await guard(async () => {
                await common.json(`AniDoki/callbackPreview?address=${encodeURIComponent(draft.callbackUrl ?? '')}`, { signal });
                const latest = await ApiClient.getPluginConfiguration(common.pluginId);
                const merged = state.mergeChanges(latest, baseline, draft);
                if (merged.conflicts.length) throw new Error(`Changed on the server: ${merged.conflicts.join(', ')}. Your edits are retained. Reload to review the current values before saving.`);
                common.status(find('#saveStatus'), 'Saving…');
                await ApiClient.updatePluginConfiguration(common.pluginId, merged.value);
                saved = merged.value; baseline = state.createDraft(merged.value);
                // Retain zero-field drafts for users/providers that have never been saved.
                for (const item of draft.ProviderApiAuth) if (!baseline.ProviderApiAuth.some(p => p.Name === item.Name)) baseline.ProviderApiAuth.push(structuredClone(item));
                for (const item of draft.UserConfig) if (!baseline.UserConfig.some(u => u.UserId === item.UserId)) baseline.UserConfig.push(structuredClone(item));
                draft = state.createDraft(baseline); libraryValidators.clear(); libraryChoices.clear();
                if (!signal.aborted) { render(signal); common.status(find('[data-page-status]'), 'Settings saved.', 'success'); }
            });
        } catch (error) {
            const message = await common.errorMessage(error);
            if (!signal.aborted) common.status(find('#saveStatus'), message, 'error');
        }
    }
}
