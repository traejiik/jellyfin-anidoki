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
        common.bindDraftWarning(view, () => dirty(), signal);
        find('[data-matches]').hidden = false;
        common.status(find('[data-page-status]'), '');
        await Promise.all([loadList(signal), loadOrphans(signal)]);
        // Coming back to the page keeps the open season, re-read in case it changed meanwhile.
        if (detail) await openSource(detail.sourceKey, provider, signal);
    }

    const dirty = () => !!match && state.isDirty(match);
    const confirmLeave = () => !dirty() || window.confirm('Discard your changes to this match?');

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
            if (button && confirmLeave()) openSource(button.dataset.source, null, signal, { force: button.dataset.source !== detail?.sourceKey }).then(() => find('#detailTitle').focus());
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
        find('#previewUser').addEventListener('change', () => preview(signal), { signal });
        editor.bindPageActions(find('.anidoki'), {
            preview: () => preview(signal),
            save: () => save(signal),
            discard: () => { match = state.discard(match); drawEditor(); say(''); preview(signal); },
            'save-automatic': () => saveAutomatic(signal),
            'delete-shared': () => deleteShared(signal),
            reload: () => {
                const user = editingFor;
                openSource(detail.sourceKey, provider, signal, { force: true }).then(() => user && overrideOf(user) ? editCorrection(user, signal) : undefined);
            },
            'view-correction': button => viewCorrection(button.dataset.user, signal),
            'edit-correction': button => editCorrection(button.dataset.user, signal),
            'stop-editing': () => stopEditing(signal),
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
            common.status(find('#pageStatus'), failure.status === 404 ? 'That season or movie is no longer in the library.' : await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        detail = loaded;
        find('#matchDetail').hidden = false;
        find('#detailTitle').textContent = detail.title;
        const episodes = detail.episodes ?? [];
        find('#detailSummary').textContent = detail.kind === 'Movie' ? 'Movie' : `${episodes.length} ${episodes.length === 1 ? 'episode' : 'episodes'} in the library`;
        find('#sourceRows').querySelectorAll('tr').forEach(row => row.toggleAttribute('aria-current', row.querySelector('[data-source]')?.dataset.source === sourceKey));
        // Trackers in the order Settings lists them; open the one asked for, else one with a saved match, else one somebody linked.
        const order = common.providers.map(entry => entry.key);
        detail.providers = [...(detail.providers ?? [])].sort((a, b) => order.indexOf(a.provider) - order.indexOf(b.provider));
        const next = detail.providers.find(entry => entry.provider === selected) ?? detail.providers.find(entry => entry.shared)
            ?? detail.providers.find(entry => linked.has(entry.provider)) ?? detail.providers[0];
        await selectProvider(next?.provider ?? null, signal, { force });
    }

    const current = () => (detail?.providers ?? []).find(entry => entry.provider === provider);

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
        if (changed || !match) {
            editingFor = null;
            match = state.createEditor(entry.shared ? { revision: entry.shared.revision, mapping: entry.shared.mapping, note: entry.shared.note } : null);
            showIncomplete = false;
        }
        closePicker();
        find('#resolutionBadge').textContent = state.resolutionBadge(entry.shared);
        find('#automaticNote').textContent = entry.shared ? `Saved by ${entry.shared.updatedBy}, ${new Date(entry.shared.updatedUtc).toLocaleString()}.` : 'Sync finds this tracker’s entry automatically.';
        applyMode();
        find('#conflictNotice').hidden = true;
        drawEditor();
        drawHistory(entry);
        drawCorrections(entry);
        say('');
        find('#previewProgress').replaceChildren();
        automatic = undefined;
        updateButtons();
        const reading = provider;
        await loadAutomatic(signal);
        if (provider === reading) await preview(signal);
    }

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
        updateButtons();
    }

    function updateButtons() {
        const isDirty = dirty();
        find('[data-action="discard"]').disabled = !isDirty;
        find('[data-action="save"]').disabled = !isDirty;
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
        if (!current()?.shared && !editingFor)
            find('#automaticNote').textContent = automatic.mapping ? 'Sync finds this tracker’s entry automatically. Save automatic as match keeps what it finds now.'
                : `Sync finds this tracker’s entry automatically, but it cannot be saved as a match: ${state.automaticReason(automatic.reason)}.`;
        updateButtons();
    }

    // The episode map shows the draft; with nothing drafted and nothing saved, what automatic matching would save.
    function mappingToPreview() {
        const empty = !match.draft.ranges.length && !match.draft.exceptions.length;
        if (empty && !current()?.shared && automatic?.mapping) return { mapping: automatic.mapping, label: 'What automatic matching would save:' };
        if (empty) return null;
        return { mapping: state.toMapping(match.draft), label: dirty() ? 'Your draft, not saved yet:' : editingFor ? 'Their correction:' : 'The saved match:' };
    }

    async function preview(signal) {
        const target = mappingToPreview();
        const rows = find('#episodeRows');
        const table = find('.doki-episode-map').parentElement;
        if (!target) {
            find('#episodeMapNote').textContent = 'No match to show. Add a range, or save what automatic matching finds.';
            rows.replaceChildren();
            table.hidden = true;
            return;
        }
        if (target.mapping !== automatic?.mapping && state.check(match.draft).length) {
            find('#episodeMapNote').textContent = 'Finish the rows below, each with its episodes and an entry, to see the episode map.';
            rows.replaceChildren();
            table.hidden = true;
            return;
        }
        const user = find('#previewUser').value;
        const path = state.routePath(base, detail.sourceKey, provider, 'preview') + (user ? `?userId=${encodeURIComponent(user)}` : '');
        find('#episodeMapNote').textContent = 'Loading…';
        const reading = provider;
        let result;
        try {
            result = await common.json(path, { method: 'POST', body: { ranges: target.mapping.ranges, exceptions: target.mapping.exceptions }, signal });
        } catch (error) {
            if (!signal.aborted) find('#episodeMapNote').textContent = await common.errorMessage(error);
            return;
        }
        if (signal.aborted || provider !== reading) return;
        table.hidden = false;
        find('#episodeMapNote').textContent = [target.label, result.borrowedLink ? 'Titles were read through another user’s link.' : ''].filter(Boolean).join(' ');
        editor.renderEpisodeMap(rows, result.rows ?? [], state.describeRow);
        const serverIssues = [...(result.errors ?? []), ...(result.warnings ?? [])];
        if (serverIssues.length && target.mapping !== automatic?.mapping)
            say(serverIssues.map(issue => issue.message).join(' '), result.errors?.length ? 'error' : '');
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
    function say(text, tone = '') { fixPending = false; common.status(find('#editorStatus'), text, tones[tone] ?? ''); }

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

    async function saveAutomatic(signal) {
        if (!automatic?.mapping) return;
        if (dirty() && !window.confirm('Replace your draft with what automatic matching finds, and save it?')) return;
        match = state.replaceDraft(match, automatic.mapping);
        if (!match.draft.note) match = state.setNote(match, 'Saved from automatic matching');
        drawEditor();
        await put(state.saveRequest(match), 'Saved what automatic matching finds.', signal);
    }

    async function put(body, done, signal) {
        say('Saving…');
        find('[data-action="save"]').disabled = true;
        let saved;
        try {
            saved = await common.json(editingFor ? state.routePath(base, 'users', editingFor, detail.sourceKey, provider) : state.routePath(base, detail.sourceKey, provider),
                { method: 'PUT', body, signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            const refused = state.saveFailure(failure.status, failure.body);
            if (refused.conflict != null) {
                match = state.markConflict(match, refused.conflict);
                find('#conflictMessage').textContent = state.conflictMessage(refused.conflict, editingFor ? 'this correction' : 'this match');
                find('#conflictNotice').hidden = false;
            }
            say(refused.message ?? `Not saved. ${await common.errorMessage(error)}`, 'error');
            updateButtons();
            return;
        }
        if (signal.aborted) return;
        match = state.markSaved(match, saved.revision);
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
            say(failure.status === 409 ? 'Not deleted: the match changed meanwhile. Reload to see it.' : `Not deleted. ${await common.errorMessage(error)}`, 'error');
            if (failure.status === 409) find('#conflictNotice').hidden = false;
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
            const from = revision.promotedFrom ? ` from ${userName(revision.promotedFrom.split(':')[0])}’s correction` : '';
            item.append(common.element('strong', '', `r${revision.revision} · ${revision.action}${from}`), common.element('span', 'doki-help', ` ${revision.author}, ${when}`));
            const lines = common.element('ul', 'doki-help');
            lines.append(...editor.describeMapping(revision.mapping).map(line => common.element('li', '', line)));
            item.append(lines);
            return item;
        });
        find('#historyList').replaceChildren(...(items.length ? items : [common.element('li', 'doki-help', 'Nothing saved for this tracker yet.')]));
    }

    const userName = id => users.find(user => user.Id.replaceAll('-', '') === String(id).replaceAll('-', ''))?.Name ?? 'a deleted user';

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
    async function editCorrection(user, signal) {
        const override = overrideOf(user);
        if (!override || !confirmLeave()) return;
        let saved;
        try {
            saved = await common.json(state.routePath(base, 'users', user, detail.sourceKey, provider), { signal });
        } catch (error) {
            if (!signal.aborted) say(await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        editingFor = user;
        match = state.createEditor({ revision: saved.revision, mapping: saved.mapping, note: saved.note });
        showIncomplete = false;
        // Jellyfin writes user IDs with or without dashes, so the option is found the way userName finds a user.
        const select = find('#previewUser');
        select.value = [...select.options].find(option => option.value && option.value.replaceAll('-', '') === String(user).replaceAll('-', ''))?.value ?? '';
        find('#conflictNotice').hidden = true;
        applyMode();
        drawEditor();
        say('');
        find('#editorHeading').focus();
        await preview(signal);
    }

    function stopEditing(signal) {
        if (!confirmLeave()) return;
        editingFor = null;
        selectProvider(provider, signal, { force: true });
    }

    // The editor works on the shared match, or on one user's correction: the heading, and which buttons apply.
    function applyMode() {
        const name = editingFor ? overrideOf(editingFor)?.userName ?? userName(editingFor) : null;
        find('#editorHeading').textContent = name ? `Edit ${name}’s correction` : 'Edit the shared match';
        find('[data-action="stop-editing"]').hidden = !editingFor;
        find('[data-action="save-automatic"]').hidden = !!editingFor;
        find('[data-action="delete-shared"]').hidden = !!editingFor || !current()?.shared;
        // The badge still says what everyone else uses; the note must not point at the buttons this mode hides.
        if (name) find('#automaticNote').textContent = `${name}’s correction applies to them alone. Everyone else uses ${current()?.shared ? 'the shared match' : 'automatic matching'}.`;
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
        await openSource(detail.sourceKey, provider, signal, { force: editingFor === user });
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
