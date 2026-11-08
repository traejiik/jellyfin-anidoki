// The admin match manager: find a season or movie, look at what each tracker would do with it, and edit, preview,
// save, promote or delete matches through AniDoki/matches. Nothing here writes to a tracker.

const base = 'AniDoki/matches';
// Same URL as common.assetUrl, which cannot be used before common.js loads.
const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/v/${globalThis.anidokiAssetVersion ??= Date.now().toString(36)}/${name}`);

export default function (view) {
    let common, editor, state, lifetime;
    let list, page, detail, provider, match, automatic, entryPicker, users = [], linked = new Set(), showIncomplete = false, editingFor = null;
    const find = selector => view.querySelector(selector);
    const tones = { error: 'error', success: 'success' };

    view.addEventListener('viewhide', () => lifetime?.abort());
    view.addEventListener('viewshow', () => {
        lifetime?.abort(); lifetime = new AbortController(); const signal = lifetime.signal;
        start(signal).catch(async error => {
            if (signal.aborted) return;
            find('[data-page-status]').textContent = common ? await common.errorMessage(error) : 'Could not load AniDōki resources.';
        });
    });

    async function start(signal) {
        common = await import(assetUrl('common.js')); if (signal.aborted) return;
        [editor, state] = await Promise.all([import(common.assetUrl('match-editor.js')), import(common.assetUrl('match-editor-state.js'))]);
        if (signal.aborted) return;
        common.ensureStyles();
        common.setTabs(common.TabMatches, common.getTabs, view);
        common.selectPluginSidebar('AniDoki_Matches', signal, view.ownerDocument);
        list ??= state.createList();
        common.status(find('[data-page-status]'), 'Loading seasons and movies…');
        const [loadedUsers, config] = await Promise.all([ApiClient.getUsers(), ApiClient.getPluginConfiguration(common.pluginId)]);
        if (signal.aborted) return;
        users = loadedUsers;
        linked = new Set((config.UserConfig ?? []).flatMap(user => (user.UserApiAuth ?? []).map(link => link.Name)));
        find('#configurationSummary').replaceChildren(common.linkedUsersBadge(users, config.UserConfig));
        fillUsers();
        find('#sourceSearch').value = list.search;
        renderFilters();
        bind(signal);
        common.bindDraftWarning(view, () => edited(), signal);
        find('[data-matches]').hidden = false;
        common.status(find('[data-page-status]'), '');
        await Promise.all([loadList(signal), loadOrphans(signal)]);
    }

    // dirty: differs from what is saved, so Save applies. edited: changed since the dialog opened, so closing asks first;
    // a new match starts from what automatic matching finds, which is dirty but not yet edited.
    let opened = null;
    const dirty = () => !!match && state.isDirty(match);
    const edited = () => !!match && JSON.stringify(match.draft) !== opened;
    const confirmLeave = () => !edited() || window.confirm('Discard your changes to this match?');

    function bind(signal) {
        entryPicker = editor.createEntryPicker(find('#entryPicker'), {
            search: query => common.json(`${base}/entries?${new URLSearchParams({ provider, query })}`, { signal }),
            providerName: () => common.providerName(provider),
            errorMessage: common.errorMessage
        });
        find('#sourceSearchForm').addEventListener('submit', event => {
            event.preventDefault();
            list = state.setSearch(list, find('#sourceSearch').value);
            loadList(signal);
        }, { signal });
        find('#sourceFilters').addEventListener('change', event => {
            list = state.setFilter(list, event.target.value);
            loadList(signal);
        }, { signal });
        find('#previousPage').addEventListener('click', () => { list = state.setPage(list, list.page - 1, state.pageCount(page)); loadList(signal); }, { signal });
        find('#nextPage').addEventListener('click', () => { list = state.setPage(list, list.page + 1, state.pageCount(page)); loadList(signal); }, { signal });
        find('#sourceRows').addEventListener('click', event => {
            const button = event.target.closest?.('[data-source]');
            if (!button) return;
            sourceOpener = button;
            openSource(button.dataset.source, null, signal, { force: true }).then(() => { if (find('#matchDialog').open) find('#dialogTitle').focus(); });
        }, { signal });
        find('#providerTabs').addEventListener('click', event => {
            const tab = event.target.closest?.('[role="tab"]');
            if (tab && tab.dataset.provider !== provider && confirmLeave()) selectProvider(tab.dataset.provider, signal);
        }, { signal });
        find('#providerTabs').addEventListener('keydown', event => {
            // Arrow keys move between tabs, as the ARIA tabs pattern expects.
            const tabs = [...find('#providerTabs').querySelectorAll('[role="tab"]')];
            const index = tabs.indexOf(event.target);
            if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
            event.preventDefault();
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
            tabs[next].focus();
        }, { signal });
        editor.bindEditor(find('#matchEditor'), find('#entryPicker'), {
            change: (row, field, value) => {
                match = field === 'note' ? state.setNote(match, value) : state.updateRow(match, row, field, value);
                refreshChecks();
            },
            edit: (action, row, value) => {
                if (action === 'add-range') match = state.addRange(match);
                else if (action === 'add-exception') match = state.addException(match);
                else if (action === 'remove-row') match = state.removeRow(match, row);
                else if (action === 'kind') match = state.updateRow(match, row, 'kind', value);
                else if (action === 'choose-entry') return openPicker(row);
                else return;
                drawEditor();
            },
            search: query => entryPicker.search(query, signal),
            pick: index => {
                const chosen = entryPicker.result(index);
                if (!chosen) return;
                const row = entryPicker.row;
                match = state.updateRow(match, row, 'target', chosen.entry);
                closePicker();
                drawEditor();
                find(`#matchEditor [data-action="choose-entry"][data-row="${row}"]`)?.focus();
            },
            close: () => { const row = entryPicker.row; closePicker(); find(`#matchEditor [data-action="choose-entry"][data-row="${row}"]`)?.focus(); }
        }, signal);
        find('#previewUser').addEventListener('change', () => previewDraft(signal), { signal });
        // Escape steps back from the editor to the season, then closes the dialog.
        editor.bindDialog(find('#matchDialog'), () => (match ? closeEditor() : closeDialog()), () => { if (detail) closeDialog({ force: true }); }, signal);
        // A modal dialog would stay on top of the next page, and its draft would outlive it.
        signal.addEventListener('abort', () => { returnFocus = sourceOpener = null; closeDialog({ force: true }); }, { once: true });
        editor.bindPageActions(find('.anidoki'), {
            'edit-shared': button => openEditor(signal, { opener: button }),
            back: () => closeEditor(),
            'close-dialog': () => closeDialog(),
            preview: () => previewDraft(signal),
            save: () => save(signal),
            'save-automatic': () => saveAutomatic(signal),
            'delete-shared': () => deleteShared(signal),
            reload: async () => {
                const user = editingFor;
                closeEditor({ force: true });
                await openSource(detail.sourceKey, provider, signal, { force: true });
                if (signal.aborted) return;
                if (user) { if (overrideOf(user)) await editCorrection(user, signal); }
                else openEditor(signal);
            },
            'view-correction': button => viewCorrection(button.dataset.user, signal),
            'edit-correction': button => editCorrection(button.dataset.user, signal, button),
            'promote-correction': button => promote(button.dataset.user, signal),
            'delete-correction': button => deleteCorrection(button.dataset.user, signal),
            'reattach-orphan': button => reattach(button.dataset.source, signal),
            'delete-orphan': button => deleteOrphan(button.dataset.source, signal)
        }, signal);
    }

    function fillUsers() {
        const select = find('#previewUser'), selected = select.value;
        select.replaceChildren(new Option('Nobody (episode map only)', ''));
        users.forEach(user => select.add(new Option(user.Name, user.Id)));
        if ([...select.options].some(option => option.value === selected)) select.value = selected;
    }

    function renderFilters() {
        find('#sourceFilters').replaceChildren(...state.filters.map(filter => {
            const label = common.element('label', 'doki-filter-choice');
            const input = common.element('input');
            Object.assign(input, { type: 'radio', name: 'matchFilter', value: filter.key, checked: filter.key === list.filter });
            label.append(input, common.element('span', '', filter.label));
            return label;
        }));
    }

    async function loadList(signal) {
        common.status(find('#pageStatus'), 'Loading…');
        try {
            page = await common.json(state.listPath(base, list), { signal });
        } catch (error) {
            if (!signal.aborted) common.status(find('#pageStatus'), await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        const rows = (page.items ?? []).map(item => {
            const row = common.element('tr');
            const open = common.element('button', 'doki-button', 'Open');
            open.type = 'button';
            open.dataset.source = item.sourceKey;
            open.setAttribute('aria-label', `Open ${item.title}`);
            if (detail?.sourceKey === item.sourceKey) row.setAttribute('aria-current', 'true');
            const saved = (item.providers ?? []).map(entry => `${common.providerName(entry.provider)}: ${state.stateLabel(entry)}`).join(', ');
            const cell = common.element('td');
            cell.append(open);
            row.append(common.element('td', 'doki-row-name', item.title), common.element('td', 'doki-muted', saved || 'Automatic'), cell);
            return row;
        });
        if (!rows.length) {
            const empty = common.element('td', 'doki-muted', list.search || list.filter ? 'Nothing matches this search.' : 'No seasons or movies found.');
            empty.colSpan = 3;
            const row = common.element('tr');
            row.append(empty);
            rows.push(row);
        }
        find('#sourceRows').replaceChildren(...rows);
        const count = state.pageCount(page);
        common.status(find('#pageStatus'), `Page ${page.page ?? 1} of ${count} · ${page.total ?? 0} ${page.total === 1 ? 'item' : 'items'}`);
        find('#previousPage').disabled = list.page <= 1;
        find('#nextPage').disabled = list.page >= count;
    }

    async function openSource(sourceKey, selected, signal, { force = false } = {}) {
        let loaded;
        try {
            loaded = await common.json(state.routePath(base, sourceKey), { signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            const text = failure.status === 404 ? 'That season or movie is no longer in the library.' : await common.errorMessage(error);
            common.status(find(find('#matchDialog').open ? '#detailStatus' : '#pageStatus'), text, 'error');
            return;
        }
        if (signal.aborted) return;
        detail = loaded;
        showSourceHeading();
        if (!find('#matchDialog').open) find('#matchDialog').showModal();
        find('#sourceRows').querySelectorAll('tr').forEach(row => row.toggleAttribute('aria-current', row.querySelector('[data-source]')?.dataset.source === sourceKey));
        // Trackers in the order Settings lists them; open the one asked for, else one with a saved match, else one somebody linked.
        const order = common.providers.map(entry => entry.key);
        detail.providers = [...(detail.providers ?? [])].sort((a, b) => order.indexOf(a.provider) - order.indexOf(b.provider));
        const next = detail.providers.find(entry => entry.provider === selected) ?? detail.providers.find(entry => entry.shared)
            ?? detail.providers.find(entry => linked.has(entry.provider)) ?? detail.providers[0];
        await selectProvider(next?.provider ?? null, signal, { force });
    }

    const current = () => (detail?.providers ?? []).find(entry => entry.provider === provider);
    let sourceOpener = null;

    function showSourceHeading() {
        const episodes = detail.episodes ?? [];
        find('#dialogTitle').textContent = detail.title;
        find('#dialogSummary').textContent = detail.kind === 'Movie' ? 'Movie' : `${episodes.length} ${episodes.length === 1 ? 'episode' : 'episodes'} in the library`;
    }

    // The dialog shows the season, or the editor in its place with a way back.
    function showView(name) {
        find('#matchDialog').querySelectorAll('[data-view]').forEach(node => { node.hidden = node.dataset.view !== name; });
    }

    // Returns false when the user keeps their unsaved changes.
    function closeDialog({ force = false } = {}) {
        if (!closeEditor({ force, refocus: false })) return false;
        const dialog = find('#matchDialog');
        if (dialog.open) dialog.close();
        detail = null;
        find('#sourceRows').querySelectorAll('tr[aria-current]').forEach(row => row.removeAttribute('aria-current'));
        if (sourceOpener?.isConnected) sourceOpener.focus();
        sourceOpener = null;
        return true;
    }

    async function selectProvider(name, signal, { force = false } = {}) {
        const changed = force || name !== provider;
        provider = name;
        find('#providerTabs').replaceChildren(...(detail.providers ?? []).map(entry => {
            const tab = common.element('button', 'doki-provider-tab', common.providerName(entry.provider));
            tab.type = 'button';
            tab.setAttribute('role', 'tab');
            tab.dataset.provider = entry.provider;
            tab.setAttribute('aria-selected', String(entry.provider === provider));
            tab.tabIndex = entry.provider === provider ? 0 : -1;
            if (entry.shared) tab.append(common.element('span', 'doki-badge', `r${entry.shared.revision}`));
            return tab;
        }));
        const entry = current();
        if (!entry) return;
        // A draft belongs to one season and tracker; the dialog is modal, so this only happens after a save or reload.
        if (changed) closeEditor({ force: true });
        find('#resolutionBadge').textContent = state.resolutionBadge(entry.shared);
        find('#automaticNote').textContent = entry.shared ? `Saved by ${entry.shared.updatedBy}, ${new Date(entry.shared.updatedUtc).toLocaleString()}.` : 'Sync finds the entry itself.';
        find('[data-action="edit-shared"]').textContent = entry.shared ? 'Edit match' : 'Create match';
        find('[data-action="delete-shared"]').hidden = !entry.shared;
        drawHistory(entry);
        drawCorrections(entry);
        say('');
        automatic = undefined;
        updateButtons();
        const reading = provider;
        await loadAutomatic(signal);
        if (provider === reading) await previewPage(signal);
    }

    // The editor opens on the shared match, or on one user's correction (D12); a new shared match starts from what
    // automatic matching finds. Closing it, saved or not, ends the mode.
    function openEditor(signal, { user = null, saved = null, opener = null } = {}) {
        const entry = current();
        if (!entry) return;
        const name = common.providerName(provider);
        editingFor = user;
        const source = user ? saved : entry.shared;
        match = state.createEditor(source ? { revision: source.revision, mapping: source.mapping, note: source.note } : null);
        if (!source && automatic?.mapping) match = state.replaceDraft(match, automatic.mapping);
        opened = JSON.stringify(match.draft);
        showIncomplete = false;
        returnFocus = opener;
        const userName = user ? overrideOf(user)?.userName ?? userNameOf(user) : null;
        const heading = user ? `Edit ${userName}’s ${name} correction` : entry.shared ? `Edit the shared ${name} match` : `Create a shared ${name} match`;
        find('#crumbSource').textContent = detail.title;
        find('#crumbEditor').textContent = heading;
        find('#dialogTitle').textContent = heading;
        find('#dialogSummary').textContent = user
            ? `${userName}’s correction applies to them alone; everyone else uses ${entry.shared ? 'the shared match' : 'automatic matching'}.`
            : 'Applies to everyone without a correction of their own.';
        if (user) {
            // Jellyfin writes user IDs with or without dashes, so the option is found the way userNameOf finds a user.
            const select = find('#previewUser');
            select.value = [...select.options].find(option => option.value && option.value.replaceAll('-', '') === String(user).replaceAll('-', ''))?.value ?? '';
        }
        find('#conflictNotice').hidden = true;
        drawEditor();
        showView('editor');
        say('');
        find('#matchDialog .doki-dialog-body[data-view="editor"]').scrollTop = 0;
        find('#matchEditor input, #matchEditor button')?.focus();
        previewDraft(signal);
    }

    // Back from the editor to the season. Returns false when the user keeps their unsaved changes.
    function closeEditor({ force = false, refocus = true } = {}) {
        if (!match) return true;
        if (!force && !confirmLeave()) return false;
        match = null;
        editingFor = null;
        closePicker();
        hideMap(draftMap(), '');
        find('#previewProgress').replaceChildren();
        find('#editorStatus').textContent = '';
        showView('detail');
        if (detail) showSourceHeading();
        if (refocus && returnFocus?.isConnected) returnFocus.focus();
        returnFocus = null;
        return true;
    }

    let returnFocus = null;

    function drawEditor() {
        editor.renderEditor(find('#matchEditor'), match, visibleIssues());
        refreshChecks();
    }

    // A row just added is not wrong yet: blanks are only pointed out once the user tries to save.
    const visibleIssues = () => state.check(match.draft).filter(issue => showIncomplete || issue.rule !== 'input');

    // Runs on every keystroke: marks rows, lists what the server would refuse, and enables Save and Discard.
    function refreshChecks() {
        const issues = visibleIssues();
        editor.renderIssues(find('#matchEditor'), find('#editorIssues'), issues);
        // "Fix the problems" stops being true once the last one is fixed.
        if (!issues.length && fixPending) say('');
        // A preview of an older draft says so, rather than passing for this one.
        if (!draftMap().table.hidden) draftMap().note.textContent = 'Changed since this preview. Select Preview to update it.';
        updateButtons();
    }

    function updateButtons() {
        find('[data-action="save"]').disabled = !dirty();
        const automaticButton = find('[data-action="save-automatic"]');
        automaticButton.disabled = !automatic?.mapping;
        automaticButton.title = automatic === undefined ? 'Checking what automatic matching finds…'
            : automatic?.mapping ? '' : `Automatic matching cannot be saved as a match: ${state.automaticReason(automatic?.reason)}.`;
    }

    async function loadAutomatic(signal) {
        const reading = provider;
        try {
            automatic = await common.json(state.routePath(base, detail.sourceKey, provider, 'automatic'), { signal });
        } catch (error) {
            if (signal.aborted) return;
            automatic = { reason: await common.errorMessage(error) };
        }
        if (signal.aborted || provider !== reading) return;
        if (!current()?.shared)
            find('#automaticNote').textContent = automatic.mapping ? 'Sync finds the entry itself.' : `Nothing to save: ${state.automaticReason(automatic.reason)}.`;
        updateButtons();
    }

    const pageMap = () => ({ rows: find('#episodeRows'), note: find('#episodeMapNote'), table: find('#episodeRows').closest('.doki-table-shell') });
    const draftMap = () => ({ rows: find('#draftRows'), note: find('#draftMapNote'), table: find('#draftRows').closest('.doki-table-shell') });

    function hideMap(map, text) {
        map.note.textContent = text;
        map.rows.replaceChildren();
        map.table.hidden = true;
    }

    // Asks the server where each episode goes under mapping, and draws it. Returns the preview, or null.
    async function drawMap(map, mapping, label, signal, user = '') {
        const path = state.routePath(base, detail.sourceKey, provider, 'preview') + (user ? `?userId=${encodeURIComponent(user)}` : '');
        map.note.textContent = 'Loading…';
        const reading = provider;
        let result;
        try {
            result = await common.json(path, { method: 'POST', body: { ranges: mapping.ranges, exceptions: mapping.exceptions }, signal });
        } catch (error) {
            if (!signal.aborted) hideMap(map, await common.errorMessage(error));
            return null;
        }
        if (signal.aborted || provider !== reading) return null;
        map.table.hidden = false;
        map.note.textContent = [label, result.borrowedLink ? 'Titles were read through another user’s link.' : ''].filter(Boolean).join(' ');
        editor.renderEpisodeMap(map.rows, result.rows ?? [], state.describeRow);
        return result;
    }

    // The page shows what sync uses now for everyone without a correction.
    async function previewPage(signal) {
        const shared = current()?.shared;
        if (shared) return drawMap(pageMap(), shared.mapping, 'The shared match:', signal);
        if (automatic?.mapping) return drawMap(pageMap(), automatic.mapping, 'What automatic matching finds now:', signal);
        hideMap(pageMap(), 'No episode map to show.');
    }

    // The dialog shows the draft, with the chosen user's progress through it.
    async function previewDraft(signal) {
        if (!match) return;
        const map = draftMap();
        find('#previewProgress').replaceChildren();
        if (!match.draft.ranges.length && !match.draft.exceptions.length) return hideMap(map, 'Add a range to see where each episode would go.');
        if (state.check(match.draft).length) return hideMap(map, 'Finish each row, with its episodes and an entry, to see where each episode would go.');
        const user = find('#previewUser').value;
        const label = dirty() ? 'Your draft, not saved yet:' : editingFor ? 'Their correction, as saved:' : 'The shared match, as saved:';
        const result = await drawMap(map, state.toMapping(match.draft), label, signal, user);
        if (!result || !match) return;
        const serverIssues = [...(result.errors ?? []), ...(result.warnings ?? [])];
        if (serverIssues.length) say(serverIssues.map(issue => issue.message).join(' '), result.errors?.length ? 'error' : '');
        drawProgress(result.progress, user);
    }

    function drawProgress(progress, user) {
        const container = find('#previewProgress');
        if (!user) return container.replaceChildren();
        const name = users.find(entry => entry.Id === user)?.Name ?? 'This user';
        if (!progress) return container.replaceChildren(common.element('p', 'doki-help', `${name} has not linked ${common.providerName(provider)}, so there is no progress to show.`));
        const items = progress.map(entry => common.element('li', '', state.progressLine(entry)));
        const listNode = common.element('ul', 'doki-progress-list');
        listNode.append(...items);
        container.replaceChildren(common.element('p', 'doki-help', `${name}’s progress through this match:`), items.length ? listNode : common.element('p', 'doki-help', 'Nothing watched yet.'));
    }

    let fixPending = false;
    // The editor speaks in its footer; the season, below its actions.
    function say(text, tone = '') { fixPending = false; common.status(find(match ? '#editorStatus' : '#detailStatus'), text, tones[tone] ?? ''); }

    async function save(signal) {
        showIncomplete = true;
        if (state.check(match.draft).length) {
            drawEditor();
            say('Fix the problems listed above, then save again.', 'error');
            fixPending = true;
            find('#matchEditor [data-invalid] input, #matchEditor [data-invalid] button')?.focus();
            return;
        }
        await put(state.saveRequest(match), 'Saved.', signal);
    }

    // From the page, with no dialog: saves what automatic matching finds as the shared match.
    async function saveAutomatic(signal) {
        const shared = current()?.shared;
        if (!automatic?.mapping) return;
        let draft = state.replaceDraft(state.createEditor(shared ? { revision: shared.revision, mapping: shared.mapping, note: shared.note } : null), automatic.mapping);
        if (!draft.draft.note) draft = state.setNote(draft, 'Saved from automatic matching');
        await put(state.saveRequest(draft), 'Saved what automatic matching finds.', signal);
    }

    async function put(body, done, signal) {
        const inDialog = !!match;
        const path = editingFor ? state.routePath(base, 'users', editingFor, detail.sourceKey, provider) : state.routePath(base, detail.sourceKey, provider);
        say('Saving…');
        if (inDialog) find('[data-action="save"]').disabled = true;
        let saved;
        try {
            saved = await common.json(path, { method: 'PUT', body, signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            const refused = state.saveFailure(failure.status, failure.body);
            if (refused.conflict != null && inDialog) {
                match = state.markConflict(match, refused.conflict);
                find('#conflictMessage').textContent = state.conflictMessage(refused.conflict, editingFor ? 'this correction' : 'this match');
                find('#conflictNotice').hidden = false;
            }
            say(refused.message ?? `Not saved. ${await common.errorMessage(error)}`, 'error');
            if (inDialog) updateButtons();
            else if (refused.conflict != null) await openSource(detail.sourceKey, provider, signal, { force: true });
            return;
        }
        if (signal.aborted) return;
        closeEditor({ force: true });
        const warnings = (saved.warnings ?? []).map(issue => issue.message);
        await openSource(detail.sourceKey, provider, signal);
        say([`${done} Revision ${saved.revision}.`, ...warnings].join(' '), warnings.length ? '' : 'success');
        loadList(signal);
    }

    async function deleteShared(signal) {
        const shared = current()?.shared;
        if (!shared || !window.confirm(`Delete the shared ${common.providerName(provider)} match? Sync goes back to matching this ${detail.kind === 'Movie' ? 'movie' : 'season'} automatically.`)) return;
        try {
            await common.json(`${state.routePath(base, detail.sourceKey, provider)}?expectedRevision=${shared.revision}`, { method: 'DELETE', signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            if (failure.status === 409) await openSource(detail.sourceKey, provider, signal, { force: true });
            if (signal.aborted) return;
            say(failure.status === 409 ? 'Not deleted: the match changed meanwhile. It is shown as it is now.' : `Not deleted. ${await common.errorMessage(error)}`, 'error');
            return;
        }
        if (signal.aborted) return;
        await openSource(detail.sourceKey, provider, signal, { force: true });
        say('Deleted.', 'success');
        loadList(signal);
    }

    function drawHistory(entry) {
        const items = [...(entry.history ?? [])].reverse().map(revision => {
            const item = common.element('li');
            const when = new Date(revision.createdUtc).toLocaleString();
            const from = revision.promotedFrom ? ` from ${userNameOf(revision.promotedFrom.split(':')[0])}’s correction` : '';
            item.append(common.element('strong', '', `r${revision.revision} · ${revision.action}${from}`), common.element('span', 'doki-help', ` ${revision.author}, ${when}`));
            const lines = common.element('ul', 'doki-help');
            lines.append(...editor.describeMapping(revision.mapping).map(line => common.element('li', '', line)));
            item.append(lines);
            return item;
        });
        find('#historyList').replaceChildren(...(items.length ? items : [common.element('li', 'doki-help', 'Nothing saved for this tracker yet.')]));
    }

    const userNameOf = id => users.find(user => user.Id.replaceAll('-', '') === String(id).replaceAll('-', ''))?.Name ?? 'a deleted user';

    function drawCorrections(entry) {
        const overrides = entry.overrides ?? [];
        find('#correctionsShell').hidden = overrides.length === 0;
        find('#correctionView').hidden = true;
        find('#correctionsNote').textContent = overrides.length
            ? 'A user’s correction replaces the shared match for them alone. Promote one to make it the shared match.'
            : 'Nobody has corrected this tracker’s match for themselves.';
        find('#correctionRows').replaceChildren(...overrides.map(override => {
            const row = common.element('tr');
            const actions = common.element('td');
            const group = common.element('div', 'doki-actions');
            for (const [action, text] of [['view-correction', 'View'], ['edit-correction', 'Edit'], ['promote-correction', 'Promote'], ['delete-correction', 'Delete']]) {
                const button = common.element('button', 'doki-button', text);
                button.type = 'button';
                button.dataset.action = action;
                button.dataset.user = override.userId;
                button.setAttribute('aria-label', `${text} ${override.userName}’s correction`);
                group.append(button);
            }
            actions.append(group);
            const revision = common.element('td', '', `r${override.revision}`);
            if (override.stale) revision.append(' ', common.element('span', 'doki-badge', 'shared match changed since'));
            row.append(common.element('td', 'doki-row-name', override.userName), revision, actions);
            return row;
        }));
    }

    const overrideOf = user => current()?.overrides?.find(entry => entry.userId === user);

    // D12: an admin edits a user's correction for them. It is saved as theirs, through the users/ routes.
    async function editCorrection(user, signal, opener = null) {
        if (!overrideOf(user)) return;
        let saved;
        try {
            saved = await common.json(state.routePath(base, 'users', user, detail.sourceKey, provider), { signal });
        } catch (error) {
            if (!signal.aborted) say(await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        openEditor(signal, { user, saved, opener });
    }

    async function viewCorrection(user, signal) {
        const override = overrideOf(user);
        if (!override) return;
        let view;
        try {
            view = await common.json(state.routePath(base, 'users', user, detail.sourceKey, provider), { signal });
        } catch (error) {
            if (!signal.aborted) say(await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        const container = find('#correctionView');
        const lines = common.element('ul');
        lines.append(...editor.describeMapping(view.mapping).map(line => common.element('li', '', line)));
        container.replaceChildren(common.element('h4', '', `${override.userName}’s correction, r${view.revision}`), lines,
            ...(view.note ? [common.element('p', 'doki-help', view.note)] : []));
        container.hidden = false;
    }

    async function promote(user, signal) {
        const override = overrideOf(user);
        if (!override || !confirmLeave()) return;
        if (!window.confirm(`Make ${override.userName}’s correction the shared ${common.providerName(provider)} match? It replaces the shared match for everyone without a correction, and ${override.userName}’s correction is removed.`)) return;
        let result;
        try {
            result = await common.json(state.routePath(base, 'overrides', user, detail.sourceKey, provider, 'promote'), { method: 'POST', signal,
                body: { expectedSharedRevision: current()?.shared?.revision ?? 0, expectedOverrideRevision: override.revision, removeOverride: true } });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            say(failure.status === 409 ? 'Not promoted: the shared match or the correction changed meanwhile. The page now shows them as they are.'
                : failure.status === 422 ? ['Not promoted.', ...(failure.body?.errors ?? []).map(issue => issue.message)].join(' ')
                    : `Not promoted. ${await common.errorMessage(error)}`, 'error');
            if (failure.status === 409) await openSource(detail.sourceKey, provider, signal, { force: true });
            return;
        }
        if (signal.aborted) return;
        await openSource(detail.sourceKey, provider, signal, { force: true });
        const others = result.differingOverrides ?? 0;
        say(`Promoted to shared revision ${result.sharedRevision}.${others ? ` ${others} other ${others === 1 ? 'user’s correction differs' : 'users’ corrections differ'} from it.` : ''}`, 'success');
        loadList(signal);
    }

    async function deleteCorrection(user, signal) {
        const override = overrideOf(user);
        if (!override || !window.confirm(`Delete ${override.userName}’s correction? They go back to the shared match, or to automatic matching.`)) return;
        try {
            await common.json(`${state.routePath(base, 'users', user, detail.sourceKey, provider)}?expectedRevision=${override.revision}`, { method: 'DELETE', signal });
        } catch (error) {
            if (!signal.aborted) say(`Not deleted. ${await common.errorMessage(error)}`, 'error');
            return;
        }
        if (signal.aborted) return;
        await openSource(detail.sourceKey, provider, signal);
        say(`Deleted ${override.userName}’s correction.`, 'success');
        loadList(signal);
    }

    let orphans = [];

    async function loadOrphans(signal) {
        try {
            orphans = await common.json(`${base}/orphans`, { signal }) ?? [];
        } catch (error) {
            if (!signal.aborted) common.status(find('#orphanStatus'), await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        // A message about the last orphan stays in view until the page is next loaded.
        find('#orphanPanel').hidden = orphans.length === 0 && !find('#orphanStatus').textContent;
        if (!orphans.length) return find('#orphanRows').replaceChildren(common.element('li', 'doki-help', 'No orphaned matches left.'));
        find('#orphanRows').replaceChildren(...orphans.map(orphan => {
            const item = common.element('li', 'doki-orphan');
            const select = common.element('select', 'doki-input');
            select.id = `orphan-target-${orphans.indexOf(orphan)}`;
            select.dataset.orphan = orphan.sourceKey;
            select.add(new Option(orphan.candidates?.length ? 'Choose where it belongs' : 'No likely match in the library', ''));
            (orphan.candidates ?? []).forEach(candidate => select.add(new Option(candidate.title, candidate.sourceKey)));
            const label = common.element('label', 'doki-sr-only', `Re-attach ${orphan.title} to`);
            label.htmlFor = select.id;
            const actions = common.element('div', 'doki-actions');
            for (const [action, text] of [['reattach-orphan', 'Re-attach'], ['delete-orphan', 'Delete']]) {
                const button = common.element('button', 'doki-button', text);
                button.type = 'button';
                button.dataset.action = action;
                button.dataset.source = orphan.sourceKey;
                button.setAttribute('aria-label', `${text} ${orphan.title}`);
                actions.append(button);
            }
            item.append(common.element('strong', '', orphan.title), common.element('p', 'doki-help', state.orphanSummary(orphan, common.providerName)),
                label, select, actions);
            return item;
        }));
    }

    async function reattach(sourceKey, signal) {
        const orphan = orphans.find(entry => entry.sourceKey === sourceKey);
        const target = find(`#orphanRows select[data-orphan="${CSS.escape(sourceKey)}"]`)?.value;
        if (!orphan) return;
        if (!target) return common.status(find('#orphanStatus'), `Choose where ${orphan.title} belongs first.`, 'error');
        const title = orphan.candidates.find(candidate => candidate.sourceKey === target)?.title ?? 'that season';
        if (!window.confirm(`Move every match and correction saved for ${orphan.title} to ${title}?`)) return;
        try {
            await common.json(`${state.routePath(base, 'orphans', sourceKey, 'reattach')}`, { method: 'POST', body: { targetSourceKey: target }, signal });
        } catch (error) {
            if (!signal.aborted) common.status(find('#orphanStatus'), `Not re-attached. ${await common.errorMessage(error)}`, 'error');
            return;
        }
        if (signal.aborted) return;
        common.status(find('#orphanStatus'), `Re-attached to ${title}.`, 'success');
        await Promise.all([loadOrphans(signal), loadList(signal)]);
    }

    async function deleteOrphan(sourceKey, signal) {
        const orphan = orphans.find(entry => entry.sourceKey === sourceKey);
        if (!orphan || !window.confirm(`Delete every match, correction and revision saved for ${orphan.title}? This cannot be undone.`)) return;
        try {
            await common.json(state.routePath(base, 'orphans', sourceKey), { method: 'DELETE', signal });
        } catch (error) {
            if (!signal.aborted) common.status(find('#orphanStatus'), `Not deleted. ${await common.errorMessage(error)}`, 'error');
            return;
        }
        if (signal.aborted) return;
        common.status(find('#orphanStatus'), `Deleted the matches saved for ${orphan.title}.`, 'success');
        await loadOrphans(signal);
    }

    function openPicker(row) {
        const target = [...match.draft.ranges, ...match.draft.exceptions].find(item => item.key === row)?.target;
        entryPicker.open(row, target?.title ?? detail.title.replace(/ · season \d+$/, ''));
    }

    const closePicker = () => entryPicker?.close();
}
