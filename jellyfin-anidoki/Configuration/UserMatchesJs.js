// Tracker matches, under Advanced on the user's own settings page: a popup listing which entry each of their seasons
// syncs to on each tracker they linked, and their own corrections (D1, D2). Saves take effect at once, apart from the page's Save changes.
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

// cache is the settings page's per-view cache: the list's search, filter and page survive Plugin Pages showing the page
// again. The popup does not: it closes when the page goes.
export default async function (section, common, cache, signal) {
    const [editor, state] = await Promise.all([import(common.assetUrl('match-editor.js')), import(common.assetUrl('match-editor-state.js'))]);
    if (signal.aborted) return;
    const find = selector => section.querySelector(selector);
    const memory = cache.matches ??= { list: null };
    let page, detail, provider, match = null, automatic, showIncomplete = false, fixPending = false;
    // shown: the popup is open. problems: how many seasons need a match. The openers get focus back.
    let shown = false, problems = 0, listOpener = null, sourceOpener = null;
    const editing = () => match != null;
    // dirty: differs from what is saved, so Save applies. edited: changed since the dialog opened, so closing asks first;
    // a first correction starts from what applies now, which is dirty but not yet edited.
    let opened = null;
    const dirty = () => editing() && state.isDirty(match);
    const edited = () => editing() && JSON.stringify(match.draft) !== opened;
    const confirmLeave = () => !edited() || window.confirm('Discard your changes to this match?');
    const current = () => (detail?.providers ?? []).find(entry => entry.provider === provider);
    const entryPicker = editor.createEntryPicker(find('#entryPicker'), {
        search: query => common.json(`${base}/entries?${new URLSearchParams({ provider, query })}`, { signal }),
        providerName: () => common.providerName(provider),
        errorMessage: common.errorMessage
    });
    cache.matchesDirty = edited;
    // The settings page enables every button again after its own actions; put Save, Discard and the pager back as they were.
    cache.matchesRefresh = () => { updateButtons(); updatePager(); };
    // A draft does not outlive its fragment, so the next showing must not warn about it before its own section loads.
    signal.addEventListener('abort', () => {
        if (cache.matchesDirty === edited) { cache.matchesDirty = null; cache.matchesRefresh = null; }
    }, { once: true });

    guardSettingsForm(section, signal);
    bind();
    // A modal dialog would stay on top of the next page, and its draft would outlive it.
    signal.addEventListener('abort', () => { shown = false; match = null; if (find('#userMatchDialog').open) find('#userMatchDialog').close(); }, { once: true });
    await countProblems();
    if (signal.aborted) return;
    common.status(find('#matchesStatus'), '');
    find('[data-action="open-matches"]').disabled = false;

    // The row says when seasons need a match, so problems show without opening the list.
    async function countProblems() {
        try {
            problems = (await common.json(state.listPath(base, state.setFilter(state.createList(), 'needs-match', state.userFilters)), { signal }))?.total ?? 0;
        } catch {
            problems = 0;
        }
        if (signal.aborted) return;
        const alert = find('#matchesProblems');
        alert.hidden = problems === 0;
        alert.textContent = `${problems} ${problems === 1 ? 'season needs' : 'seasons need'} a match`;
    }

    // Problems first: the list opens on Needs a match while there are any, else as it was left.
    function openMatches(button) {
        listOpener = button;
        memory.list = problems ? state.setFilter(state.createList(), 'needs-match', state.userFilters) : memory.list ?? state.createList();
        find('#userSourceSearch').value = memory.list.search;
        renderFilters();
        showList();
        shown = true;
        find('#userMatchDialog').showModal();
        find('#userSourceSearch').focus();
        loadList();
    }

    function showList() {
        showView('list');
        find('#userDialogTitle').textContent = 'Tracker matches';
        find('#userDialogSummary').textContent = 'Which tracker entry each of your seasons syncs to.';
    }

    // Back from a season to the list. Returns false when the user keeps their unsaved changes.
    function backToList({ force = false } = {}) {
        if (!force && !confirmLeave()) return false;
        leaveEditor();
        detail = null;
        showList();
        find('#userSourceRows').querySelectorAll('tr[aria-current]').forEach(row => row.removeAttribute('aria-current'));
        loadList().then(() => {
            const again = sourceOpener && find(`#userSourceRows [data-source="${CSS.escape(sourceOpener.dataset.source)}"]`);
            (again ?? find('#userSourceSearch')).focus();
            sourceOpener = null;
        });
        return true;
    }

    // Breadcrumbs: every level but the last goes back to it.
    function drawCrumbs(levels) {
        find('#userCrumbs').replaceChildren(...levels.map(([label, action], index) => {
            const item = common.element('li');
            if (index === levels.length - 1) {
                const here = common.element('span', '', label);
                here.setAttribute('aria-current', 'page');
                item.append(here);
            } else {
                const go = common.element('button', '', label);
                go.type = 'button';
                go.dataset.action = action;
                item.append(go);
            }
            return item;
        }));
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
            if (!button) return;
            sourceOpener = button;
            openSource(button.dataset.source, null, { force: true }).then(() => { if (detail) find('#userDialogTitle').focus(); });
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
        // Escape steps back a level: editor to season, season to list, list to closed.
        editor.bindDialog(find('#userMatchDialog'), () => (editing() ? cancelCorrection() : detail ? backToList() : closeDialog()), () => { if (shown) closeDialog({ force: true }); }, signal);
        editor.bindPageActions(section, {
            'search-sources': search,
            'previous-page': () => { memory.list = state.setPage(memory.list, memory.list.page - 1, state.pageCount(page)); loadList(); },
            'next-page': () => { memory.list = state.setPage(memory.list, memory.list.page + 1, state.pageCount(page)); loadList(); },
            correct: startCorrection,
            'use-shared': useShared,
            preview: previewDraft,
            save,
            'open-matches': openMatches,
            back: () => (editing() ? cancelCorrection() : backToList()),
            'back-to-season': cancelCorrection,
            'back-to-list': () => backToList(),
            'close-dialog': () => closeDialog(),
            reload: () => { leaveEditor(); openSource(detail.sourceKey, provider, { force: true }).then(() => { if (!signal.aborted) startCorrection(); }); }
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
        // An empty list says so in the space the table would fill, rather than as a lone row.
        find('#userSourceEmpty').textContent = memory.list.search ? 'Nothing matches this search.'
            : memory.list.filter === 'needs-match' ? 'Every season you synced recently found its entry.'
                : memory.list.filter ? 'Nothing to show here.' : 'No seasons or movies in the libraries you sync.';
        find('#userSourceEmpty').hidden = rows.length > 0;
        find('#userSourceRows').closest('.doki-table-shell').hidden = rows.length === 0;
        find('#userSourceRows').replaceChildren(...rows);
        common.status(find('#userPageStatus'), `Page ${page.page ?? 1} of ${state.pageCount(page)} · ${page.total ?? 0} ${page.total === 1 ? 'item' : 'items'}`);
        updatePager();
    }

    function updatePager() {
        if (!memory.list) return;
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
            const failure = await editor.problem(error);
            const text = failure.status === 404 ? 'That season or movie is no longer in your libraries.' : await common.errorMessage(error);
            common.status(find(detail ? '#userEditorStatus' : '#userPageStatus'), text, 'error');
            return;
        }
        if (signal.aborted || !shown) return;
        detail = loaded;
        showView('detail');
        showSourceHeading();
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
        if (changed || !entry) leaveEditor();
        find('#userProviderPanel').hidden = !entry;
        find('#userNoProviders').hidden = !!entry;
        if (!entry) return;
        find('#userResolutionBadge').textContent = state.effectiveBadge(entry);
        find('#userResolutionNote').textContent = entry.personal ? `Your correction, saved ${new Date(entry.personal.updatedUtc).toLocaleString()}.`
            : entry.shared ? `Saved by ${entry.shared.updatedBy}, ${new Date(entry.shared.updatedUtc).toLocaleString()}.`
                : 'Sync finds the entry itself.';
        find('#userStaleNotice').hidden = !entry.personal?.stale;
        find('#userConflictNotice').hidden = true;
        automatic = undefined;
        const back = find('[data-action="use-shared"]');
        back.hidden = !entry.personal;
        back.textContent = entry.shared ? 'Use the shared match' : 'Go back to automatic matching';
        say('');
        const reading = provider;
        if (!entry.personal && !entry.shared) await loadAutomatic();
        if (provider === reading) await previewPage();
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
        find('#userResolutionNote').textContent = `No episode map: ${state.automaticReason(automatic?.reason, { own: true })}.`;
    }

    // A correction starts from what applies now: the user's own, else the shared match, else what automatic matching finds.
    function startCorrection() {
        const entry = current();
        if (!entry) return;
        match = state.createEditor(entry.personal ? { revision: entry.personal.revision, mapping: entry.personal.mapping, note: entry.personal.note } : null);
        const start = entry.personal ? null : entry.shared?.mapping ?? automatic?.mapping;
        if (start) match = state.replaceDraft(match, start);
        opened = JSON.stringify(match.draft);
        showIncomplete = false;
        const name = common.providerName(provider);
        const heading = entry.personal ? `Edit your ${name} correction` : `Correct your ${name} match`;
        drawCrumbs([['Tracker matches', 'back-to-list'], [detail.title, 'back-to-season'], [heading]]);
        find('#userDialogTitle').textContent = heading;
        find('#userDialogSummary').textContent = 'Your correction applies to you alone, and is saved straight away.';
        find('#userConflictNotice').hidden = true;
        drawEditor();
        showView('editor');
        say('');
        find('#userMatchDialog .doki-dialog-body[data-view="editor"]').scrollTop = 0;
        find('#matchEditor input, #matchEditor button')?.focus();
        previewDraft();
    }

    // Back from the editor to the season.
    function cancelCorrection() {
        if (!confirmLeave()) return false;
        leaveEditor();
        find('[data-action="correct"]').focus();
        return true;
    }

    // Drops the draft and shows the season again, saved or not.
    function leaveEditor() {
        match = null;
        showIncomplete = false;
        entryPicker.close();
        hideMap(draftMap(), '');
        find('#userDialogStatus').textContent = '';
        if (detail) {
            showView('detail');
            showSourceHeading();
        }
    }

    function showSourceHeading() {
        const episodes = detail.episodes ?? [];
        drawCrumbs([['Tracker matches', 'back-to-list'], [detail.title]]);
        find('#userDialogTitle').textContent = detail.title;
        find('#userDialogSummary').textContent = detail.kind === 'Movie' ? 'Movie' : `${episodes.length} ${episodes.length === 1 ? 'episode' : 'episodes'} in your library`;
    }

    // The dialog shows the season, or the editor in its place with a way back.
    function showView(name) {
        find('#userMatchDialog').querySelectorAll('[data-view]').forEach(node => { node.hidden = !node.dataset.view.split(' ').includes(name); });
    }

    function closeDialog({ force = false } = {}) {
        if (!shown || (!force && !confirmLeave())) return;
        shown = false;
        leaveEditor();
        detail = null;
        sourceOpener = null;
        if (find('#userMatchDialog').open) find('#userMatchDialog').close();
        if (listOpener?.isConnected) listOpener.focus();
        listOpener = null;
        // A sync may have run meanwhile, so the count is read again.
        countProblems();
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
        // A preview of an older draft says so, rather than passing for this one.
        if (!draftMap().table.hidden) draftMap().note.textContent = 'Changed since this preview. Select Preview to update it.';
        updateButtons();
    }

    function updateButtons() {
        if (editing()) find('[data-action="save"]').disabled = !dirty();
    }

    const pageMap = () => ({ rows: find('#userEpisodeRows'), note: find('#userEpisodeMapNote'), table: find('#userEpisodeRows').closest('.doki-table-shell'), progress: find('#userProgress') });
    const draftMap = () => ({ rows: find('#userDraftRows'), note: find('#userDraftMapNote'), table: find('#userDraftRows').closest('.doki-table-shell'), progress: find('#userDraftProgress') });

    function hideMap(map, text) {
        map.note.textContent = text;
        map.rows.replaceChildren();
        map.table.hidden = true;
        map.progress.replaceChildren();
    }

    // Asks the server where each episode goes under mapping, and draws it with the user's progress. Returns the preview, or null.
    async function drawMap(map, mapping, label) {
        map.note.textContent = 'Loading…';
        const reading = provider;
        let result;
        try {
            result = await common.json(state.routePath(base, detail.sourceKey, provider, 'preview'),
                { method: 'POST', body: { ranges: mapping.ranges, exceptions: mapping.exceptions }, signal });
        } catch (error) {
            if (!signal.aborted) hideMap(map, await common.errorMessage(error));
            return null;
        }
        if (signal.aborted || provider !== reading) return null;
        map.table.hidden = false;
        map.note.textContent = label;
        editor.renderEpisodeMap(map.rows, result.rows ?? [], state.describeRow);
        drawProgress(map.progress, result.progress);
        return result;
    }

    // The section shows what applies to the user now.
    async function previewPage() {
        const entry = current();
        const target = entry?.personal ? [entry.personal.mapping, 'Your correction:'] : entry?.shared ? [entry.shared.mapping, 'The shared match:']
            : automatic?.mapping ? [automatic.mapping, 'What automatic matching finds now:'] : null;
        if (!target) return hideMap(pageMap(), 'No episode map to show.');
        await drawMap(pageMap(), ...target);
    }

    // The dialog shows the draft.
    async function previewDraft() {
        if (!editing()) return;
        const map = draftMap();
        if (!match.draft.ranges.length && !match.draft.exceptions.length) return hideMap(map, 'Add a range to see where your episodes would go.');
        if (state.check(match.draft).length) return hideMap(map, 'Finish each row, with its episodes and an entry, to see where your episodes would go.');
        const result = await drawMap(map, state.toMapping(match.draft), dirty() ? 'Your draft, not saved yet:' : 'Your correction, as saved:');
        const issues = [...(result?.errors ?? []), ...(result?.warnings ?? [])];
        if (result && editing() && issues.length) say(issues.map(issue => issue.message).join(' '), result.errors?.length ? 'error' : '');
    }

    function drawProgress(container, progress) {
        if (!progress) return container.replaceChildren();
        const list = common.element('ul', 'doki-progress-list');
        list.append(...progress.map(entry => common.element('li', '', state.progressLine(entry, 'your'))));
        container.replaceChildren(common.element('p', 'doki-help', 'Your progress through this match:'), progress.length ? list : common.element('p', 'doki-help', 'Nothing watched yet.'));
    }

    // common.status takes the tone as given; 'error' and 'success' are the two it styles.
    // The editor speaks in its footer; the season, below its actions.
    function say(text, tone = '') { fixPending = false; common.status(find(editing() ? '#userDialogStatus' : '#userEditorStatus'), text, tone); }

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
        leaveEditor();
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
