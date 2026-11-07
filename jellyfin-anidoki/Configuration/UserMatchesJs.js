// The Matches section of the user's own settings page: which entry each of their seasons syncs to on each tracker
// they linked, and their own corrections (D1, D2). Saves take effect at once, apart from the page's Save changes.
// Every read goes through the user's own links (match API, Differences item 3). Nothing here writes to a tracker.

const base = 'AniDoki/user/matches';

// The section sits inside the settings form, whose submit saves preferences. Its picker's form, and Enter in its
// fields, must not reach that form. The picker and the note keep Enter.
export function guardSettingsForm(section, signal) {
    section.addEventListener('submit', event => { event.preventDefault(); event.stopPropagation(); }, { signal });
    section.addEventListener('keydown', event => {
        if (event.key === 'Enter' && event.target?.tagName === 'INPUT' && !event.target.closest?.('#entryPicker')) event.preventDefault();
    }, { signal });
}

// cache is the settings page's per-view cache: what is open survives Plugin Pages showing the page again.
export default async function (section, common, cache, signal) {
    const [editor, state] = await Promise.all([import(common.assetUrl('match-editor.js')), import(common.assetUrl('match-editor-state.js'))]);
    if (signal.aborted) return;
    const find = selector => section.querySelector(selector);
    const memory = cache.matches ??= { list: null, sourceKey: null, provider: null };
    let page, detail, provider, match = null, automatic, showIncomplete = false, fixPending = false;
    const editing = () => match != null;
    const dirty = () => editing() && state.isDirty(match);
    const confirmLeave = () => !dirty() || window.confirm('Discard your changes to this match?');
    const current = () => (detail?.providers ?? []).find(entry => entry.provider === provider);
    const entryPicker = editor.createEntryPicker(find('#entryPicker'), {
        search: query => common.json(`${base}/entries?${new URLSearchParams({ provider, query })}`, { signal }),
        providerName: () => common.providerName(provider),
        errorMessage: common.errorMessage
    });
    cache.matchesDirty = dirty;
    // The settings page enables every button again after its own actions; put Save, Discard and the pager back as they were.
    cache.matchesRefresh = () => { updateButtons(); updatePager(); };
    // A draft does not outlive its fragment, so the next showing must not warn about it before its own section loads.
    signal.addEventListener('abort', () => {
        if (cache.matchesDirty === dirty) { cache.matchesDirty = null; cache.matchesRefresh = null; }
    }, { once: true });

    guardSettingsForm(section, signal);
    bind();
    memory.list ??= await firstList();
    if (signal.aborted) return;
    find('#userSourceSearch').value = memory.list.search;
    renderFilters();
    find('[data-matches]').hidden = false;
    common.status(find('#matchesStatus'), '');
    await loadList();
    if (memory.sourceKey && !signal.aborted) await openSource(memory.sourceKey, memory.provider);

    // Problems first: open on Needs a match when the user has any.
    async function firstList() {
        const list = state.createList();
        const problems = state.setFilter(list, 'needs-match', state.userFilters);
        try {
            if ((await common.json(state.listPath(base, problems), { signal }))?.total) return problems;
        } catch {
            // The full list's own load reports what went wrong.
        }
        return list;
    }

    function bind() {
        const search = () => { memory.list = state.setSearch(memory.list, find('#userSourceSearch').value); loadList(); };
        find('#userSourceSearch').addEventListener('keydown', event => { if (event.key === 'Enter') search(); }, { signal });
        find('#userSourceFilters').addEventListener('change', event => {
            memory.list = state.setFilter(memory.list, event.target.value, state.userFilters);
            loadList();
        }, { signal });
        find('#userSourceRows').addEventListener('click', event => {
            const button = event.target.closest?.('[data-source]');
            if (button && confirmLeave()) openSource(button.dataset.source, null, { force: button.dataset.source !== detail?.sourceKey }).then(() => find('#userDetailTitle').focus());
        }, { signal });
        find('#userProviderTabs').addEventListener('click', event => {
            const tab = event.target.closest?.('[role="tab"]');
            if (tab && tab.dataset.provider !== provider && confirmLeave()) selectProvider(tab.dataset.provider);
        }, { signal });
        find('#userProviderTabs').addEventListener('keydown', event => {
            // Arrow keys move between tabs, as the ARIA tabs pattern expects.
            const tabs = [...find('#userProviderTabs').querySelectorAll('[role="tab"]')];
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
                entryPicker.close();
                drawEditor();
                find(`#matchEditor [data-action="choose-entry"][data-row="${row}"]`)?.focus();
            },
            close: () => { const row = entryPicker.row; entryPicker.close(); find(`#matchEditor [data-action="choose-entry"][data-row="${row}"]`)?.focus(); }
        }, signal);
        editor.bindPageActions(section, {
            'search-sources': search,
            'previous-page': () => { memory.list = state.setPage(memory.list, memory.list.page - 1, state.pageCount(page)); loadList(); },
            'next-page': () => { memory.list = state.setPage(memory.list, memory.list.page + 1, state.pageCount(page)); loadList(); },
            correct: startCorrection,
            'use-shared': useShared,
            preview: () => preview(),
            save,
            discard: () => { match = state.discard(match); drawEditor(); say(''); preview(); },
            cancel: cancelCorrection,
            reload: () => { match = null; openSource(detail.sourceKey, provider, { force: true }).then(startCorrection); }
        }, signal);
    }

    function renderFilters() {
        find('#userSourceFilters').replaceChildren(...state.userFilters.map(filter => {
            const label = common.element('label', 'doki-filter-choice');
            const input = common.element('input');
            Object.assign(input, { type: 'radio', name: 'userMatchFilter', value: filter.key, checked: filter.key === memory.list.filter });
            label.append(input, common.element('span', '', filter.label));
            return label;
        }));
    }

    async function loadList() {
        common.status(find('#userPageStatus'), 'Loading…');
        try {
            page = await common.json(state.listPath(base, memory.list), { signal });
        } catch (error) {
            if (!signal.aborted) common.status(find('#userPageStatus'), await common.errorMessage(error), 'error');
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
            const text = memory.list.filter === 'needs-match' ? 'Every season you synced recently found its entry.'
                : memory.list.search || memory.list.filter ? 'Nothing matches this search.' : 'No seasons or movies in the libraries you sync.';
            const empty = common.element('td', 'doki-muted', text);
            empty.colSpan = 3;
            const row = common.element('tr');
            row.append(empty);
            rows.push(row);
        }
        find('#userSourceRows').replaceChildren(...rows);
        common.status(find('#userPageStatus'), `Page ${page.page ?? 1} of ${state.pageCount(page)} · ${page.total ?? 0} ${page.total === 1 ? 'item' : 'items'}`);
        updatePager();
    }

    function updatePager() {
        const count = state.pageCount(page);
        find('[data-action="previous-page"]').disabled = memory.list.page <= 1;
        find('[data-action="next-page"]').disabled = memory.list.page >= count;
    }

    async function openSource(sourceKey, selected, { force = false } = {}) {
        let loaded;
        try {
            loaded = await common.json(state.routePath(base, sourceKey), { signal });
        } catch (error) {
            if (signal.aborted) return;
            memory.sourceKey = null;
            const failure = await editor.problem(error);
            common.status(find('#userPageStatus'), failure.status === 404 ? 'That season or movie is no longer in your libraries.' : await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted) return;
        detail = loaded;
        memory.sourceKey = sourceKey;
        find('#userMatchDetail').hidden = false;
        find('#userDetailTitle').textContent = detail.title;
        const episodes = detail.episodes ?? [];
        find('#userDetailSummary').textContent = detail.kind === 'Movie' ? 'Movie' : `${episodes.length} ${episodes.length === 1 ? 'episode' : 'episodes'} in your library`;
        find('#userSourceRows').querySelectorAll('tr').forEach(row => row.toggleAttribute('aria-current', row.querySelector('[data-source]')?.dataset.source === sourceKey));
        // Trackers in the order Settings lists them; open the one asked for, else one with a correction, else the first.
        const order = common.providers.map(entry => entry.key);
        detail.providers = [...(detail.providers ?? [])].sort((a, b) => order.indexOf(a.provider) - order.indexOf(b.provider));
        const next = detail.providers.find(entry => entry.provider === selected) ?? detail.providers.find(entry => entry.personal) ?? detail.providers[0];
        await selectProvider(next?.provider ?? null, { force });
    }

    async function selectProvider(name, { force = false } = {}) {
        const changed = force || name !== provider;
        provider = name;
        memory.provider = name;
        find('#userProviderTabs').replaceChildren(...(detail.providers ?? []).map(entry => {
            const tab = common.element('button', 'doki-provider-tab', common.providerName(entry.provider));
            tab.type = 'button';
            tab.setAttribute('role', 'tab');
            tab.dataset.provider = entry.provider;
            tab.setAttribute('aria-selected', String(entry.provider === provider));
            tab.tabIndex = entry.provider === provider ? 0 : -1;
            if (entry.personal) tab.append(common.element('span', 'doki-badge', 'yours'));
            return tab;
        }));
        const entry = current();
        // A draft belongs to one season and tracker: it goes when either changes, or when there is no tracker to show.
        if (changed || !entry) { match = null; showIncomplete = false; entryPicker.close(); }
        find('#userProviderPanel').hidden = !entry;
        find('#userNoProviders').hidden = !!entry;
        if (!entry) return;
        find('#userResolutionBadge').textContent = state.effectiveBadge(entry);
        find('#userResolutionNote').textContent = entry.personal ? `Your correction, saved ${new Date(entry.personal.updatedUtc).toLocaleString()}. It applies to you alone.`
            : entry.shared ? `The shared match, saved by ${entry.shared.updatedBy}, ${new Date(entry.shared.updatedUtc).toLocaleString()}.`
                : 'Sync finds this tracker’s entry automatically.';
        find('#userStaleNotice').hidden = !entry.personal?.stale;
        find('#userConflictNotice').hidden = true;
        automatic = undefined;
        showMode();
        say('');
        const reading = provider;
        if (!entry.personal && !entry.shared) await loadAutomatic();
        if (provider === reading) await preview();
    }

    // Looking at what applies, or editing a correction: which controls show.
    function showMode() {
        const entry = current();
        find('#userEditorShell').hidden = !editing();
        find('[data-action="correct"]').hidden = editing();
        const back = find('[data-action="use-shared"]');
        back.hidden = editing() || !entry?.personal;
        back.textContent = entry?.shared ? 'Use the shared match' : 'Go back to automatic matching';
        if (editing()) drawEditor();
        updateButtons();
    }

    async function loadAutomatic() {
        const reading = provider;
        try {
            automatic = await common.json(state.routePath(base, detail.sourceKey, provider, 'automatic'), { signal });
        } catch (error) {
            if (signal.aborted) return;
            automatic = { reason: await common.errorMessage(error) };
        }
        if (signal.aborted || provider !== reading || automatic?.mapping) return;
        find('#userResolutionNote').textContent = `Sync finds this tracker’s entry automatically, but there is no episode map to show: ${state.automaticReason(automatic?.reason, { own: true })}.`;
    }

    // A correction starts from what applies now: the user's own, else the shared match, else what automatic matching finds.
    function startCorrection() {
        const entry = current();
        if (!entry) return;
        match = state.createEditor(entry.personal ? { revision: entry.personal.revision, mapping: entry.personal.mapping, note: entry.personal.note } : null);
        const start = entry.personal ? null : entry.shared?.mapping ?? automatic?.mapping;
        if (start) match = state.replaceDraft(match, start);
        showIncomplete = false;
        showMode();
        find('#matchEditor input, #matchEditor button')?.focus();
        preview();
    }

    function cancelCorrection() {
        if (!confirmLeave()) return;
        match = null;
        entryPicker.close();
        showIncomplete = false;
        showMode();
        say('');
        find('[data-action="correct"]').focus();
        preview();
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
        if (!editing()) return;
        const isDirty = dirty();
        find('[data-action="save"]').disabled = !isDirty;
        find('[data-action="discard"]').disabled = !isDirty;
    }

    function previewTarget() {
        const entry = current();
        if (editing()) {
            const empty = !match.draft.ranges.length && !match.draft.exceptions.length;
            return empty ? null : { mapping: state.toMapping(match.draft), label: dirty() ? 'Your draft, not saved yet:' : 'Your correction:', draft: true };
        }
        if (entry?.personal) return { mapping: entry.personal.mapping, label: 'Your correction:' };
        if (entry?.shared) return { mapping: entry.shared.mapping, label: 'The shared match:' };
        if (automatic?.mapping) return { mapping: automatic.mapping, label: 'What automatic matching finds now:' };
        return null;
    }

    async function preview() {
        const target = previewTarget();
        const rows = find('#userEpisodeRows');
        const table = rows.closest('.doki-table-shell');
        const hide = text => { find('#userEpisodeMapNote').textContent = text; rows.replaceChildren(); table.hidden = true; find('#userProgress').replaceChildren(); };
        if (!target) return hide(editing() ? 'Add a range to see where your episodes would go.' : 'No episode map to show.');
        if (target.draft && state.check(match.draft).length) return hide('Finish the rows below, each with its episodes and an entry, to see the episode map.');
        find('#userEpisodeMapNote').textContent = 'Loading…';
        const reading = provider;
        let result;
        try {
            result = await common.json(state.routePath(base, detail.sourceKey, provider, 'preview'),
                { method: 'POST', body: { ranges: target.mapping.ranges, exceptions: target.mapping.exceptions }, signal });
        } catch (error) {
            if (!signal.aborted) find('#userEpisodeMapNote').textContent = await common.errorMessage(error);
            return;
        }
        if (signal.aborted || provider !== reading) return;
        table.hidden = false;
        find('#userEpisodeMapNote').textContent = target.label;
        editor.renderEpisodeMap(rows, result.rows ?? [], state.describeRow);
        const issues = [...(result.errors ?? []), ...(result.warnings ?? [])];
        if (target.draft && issues.length) say(issues.map(issue => issue.message).join(' '), result.errors?.length ? 'error' : '');
        drawProgress(result.progress);
    }

    function drawProgress(progress) {
        const container = find('#userProgress');
        if (!progress) return container.replaceChildren();
        const list = common.element('ul', 'doki-progress-list');
        list.append(...progress.map(entry => common.element('li', '', state.progressLine(entry, 'your'))));
        container.replaceChildren(common.element('p', 'doki-help', 'Your progress through this match:'), progress.length ? list : common.element('p', 'doki-help', 'Nothing watched yet.'));
    }

    // common.status takes the tone as given; 'error' and 'success' are the two it styles.
    function say(text, tone = '') { fixPending = false; common.status(find('#userEditorStatus'), text, tone); }

    async function save() {
        showIncomplete = true;
        if (state.check(match.draft).length) {
            drawEditor();
            say('Fix the problems listed above, then save again.', 'error');
            fixPending = true;
            find('#matchEditor [data-invalid] input, #matchEditor [data-invalid] button')?.focus();
            return;
        }
        say('Saving…');
        find('[data-action="save"]').disabled = true;
        let saved;
        try {
            saved = await common.json(state.routePath(base, detail.sourceKey, provider), { method: 'PUT', body: state.saveRequest(match), signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            const refused = state.saveFailure(failure.status, failure.body);
            if (refused.conflict != null) {
                match = state.markConflict(match, refused.conflict);
                find('#userConflictMessage').textContent = state.conflictMessage(refused.conflict, 'your correction');
                find('#userConflictNotice').hidden = false;
            }
            say(refused.message ?? `Not saved. ${await common.errorMessage(error)}`, 'error');
            updateButtons();
            return;
        }
        if (signal.aborted) return;
        const warnings = (saved?.warnings ?? []).map(issue => issue.message);
        match = null;
        await openSource(detail.sourceKey, provider, { force: true });
        say([`Saved your correction, revision ${saved?.revision}. Your next sync of this season uses it.`, ...warnings].join(' '), warnings.length ? '' : 'success');
        loadList();
    }

    async function useShared() {
        const entry = current();
        if (!entry?.personal) return;
        const back = entry.shared ? 'the shared match' : 'automatic matching';
        if (!window.confirm(`Delete your ${common.providerName(provider)} correction and go back to ${back}?`)) return;
        try {
            await common.json(`${state.routePath(base, detail.sourceKey, provider)}?expectedRevision=${entry.personal.revision}`, { method: 'DELETE', signal });
        } catch (error) {
            if (signal.aborted) return;
            const failure = await editor.problem(error);
            // Reopening clears the editor's status, so the refusal is said after it.
            if (failure.status === 409) await openSource(detail.sourceKey, provider, { force: true });
            if (signal.aborted) return;
            say(failure.status === 409 ? 'Not deleted: your correction changed meanwhile. It is shown as it is now.' : `Not deleted. ${await common.errorMessage(error)}`, 'error');
            return;
        }
        if (signal.aborted) return;
        await openSource(detail.sourceKey, provider, { force: true });
        say(`Deleted your correction. This season now uses ${back}.`, 'success');
        loadList();
    }

    function openPicker(row) {
        const target = [...match.draft.ranges, ...match.draft.exceptions].find(item => item.key === row)?.target;
        entryPicker.open(row, target?.title ?? detail.title.replace(/ · season \d+$/, ''));
    }
}
