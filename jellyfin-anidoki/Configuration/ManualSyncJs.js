// The value the sync endpoint reads as every linked tracker. Outbound only.
export const allTrackers = 'All';
// Outbound sends status only because the endpoint binds it; it has no effect there.
export function buildSyncRequest(form, linked) {
    if (!form.userId) throw new Error('Select a user.');
    if (!['UpdateJellyfin', 'UpdateProvider'].includes(form.syncAction)) throw new Error('Select a valid direction.');
    if (!linked.length) throw new Error('This user has no linked trackers. Link an account in Settings.');
    if (form.syncAction === 'UpdateProvider') {
        if (form.trackers !== allTrackers && !linked.includes(form.trackers)) throw new Error('Select All linked trackers or one linked tracker.');
        return { userId: form.userId, provider: form.trackers, syncAction: form.syncAction, status: 'Both' };
    }
    if (!linked.includes(form.provider)) throw new Error('Select a linked provider for this user.');
    if (!form.completed && !form.watching) throw new Error('Choose at least one inbound status.');
    return { userId: form.userId, provider: form.provider, syncAction: form.syncAction, status: form.completed && form.watching ? 'Both' : form.completed ? 'Completed' : 'Watching' };
}
// One line naming what a run does, shown above its progress.
export function describeRequest(request, userName, providerName) {
    if (request.syncAction === 'UpdateJellyfin') return `Tracker → Jellyfin · ${userName} · ${providerName(request.provider)} · ${request.status}`;
    return `Jellyfin → Trackers · ${userName} · ${request.provider === allTrackers ? 'All linked trackers' : providerName(request.provider)}`;
}
const units = { SyncToTrackers: 'seasons', SyncFromTrackers: 'tracker entries', PreviewToTrackers: 'seasons', PreviewFromTrackers: 'tracker entries' };
const previewHeadings = { Succeeded: 'Preview ready', Partial: 'Preview ready, with some problems', NoChange: 'Nothing to preview' };
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
    const preview = view.kind?.startsWith('Preview');
    const heading = (preview ? previewHeadings[view.state] : undefined) ?? headings[view.state] ?? view.state;
    // A finished preview's rows say what it found, so its line only says it is ready and how much it checked.
    if (preview && previewHeadings[view.state]) return { message: view.total ? `${heading} · ${view.total} ${units[view.kind]} checked` : heading, tone };
    // "Cancelled after 3 of 12 seasons…" already says what the heading would.
    const shown = view.message?.startsWith(heading) ? null : heading;
    return { message: [shown, counts, view.message].filter(Boolean).join('. '), tone };
}

// A run's progress bar: value and max, or a null value while the end is not known yet.
export function runProgress(state, done, total) {
    if (isFinished(state ?? 'Queued')) return total ? { value: Math.min(done ?? total, total), max: total } : { value: 1, max: 1 };
    return total ? { value: Math.min(done ?? 0, total), max: total } : { value: null, max: 1 };
}

// Same URL as common.assetUrl, which cannot be used before common.js loads.
const assetUrl = name => ApiClient.getUrl(`AniDoki/assets/v/${globalThis.anidokiAssetVersion ??= Date.now().toString(36)}/${name}`);

export default function (view) {
    // lastRun: the latest run of either kind. previewRun: the preview the dialog shows, until a sync from the page replaces it.
    let lifetime, common, previewState, users = [], config, busy = false, lastRun, previewRun;
    const find = selector => view.querySelector(selector);
    const links = () => (config?.UserConfig?.find(user => user.UserId === find('#selectSyncUser').value)?.UserApiAuth ?? []).map(auth => auth.Name);
    const options = () => ({ userId: find('#selectSyncUser').value, provider: find('#selectSyncProvider').value, trackers: find('#selectSyncTrackers').value,
        syncAction: find('input[name="syncAction"]:checked').value, completed: find('#completedStatus').checked, watching: find('#watchingStatus').checked });
    const dialog = () => find('#previewDialog');
    view.addEventListener('viewhide', () => lifetime?.abort());
    view.addEventListener('viewshow', () => {
        lifetime?.abort(); lifetime = new AbortController(); const signal = lifetime.signal;
        start(signal).catch(async error => {
            const message = common ? await common.errorMessage(error) : 'Could not load AniDōki resources.';
            if (!signal.aborted) find('[data-page-status]').textContent = message;
        });
    });
    async function start(signal) {
        [common, previewState] = await Promise.all([import(assetUrl('common.js')), import(assetUrl('preview-state.js'))]); if (signal.aborted) return;
        common.ensureStyles(); common.setTabs(common.TabManualSync, common.getTabs, view);
        find('#previousPreviewPage').replaceChildren(common.icon('chevron-left'));
        find('#nextPreviewPage').replaceChildren(common.icon('chevron-right'));
        common.selectPluginSidebar('AniDoki_ManualSync', signal, view.ownerDocument);
        // A modal dialog would stay on top of the next page.
        signal.addEventListener('abort', () => { if (dialog().open) dialog().close(); }, { once: true });
        find('#runSync').disabled = true; common.status(find('[data-page-status]'), 'Loading users and linked trackers…');
        const result = await Promise.all([ApiClient.getUsers(), ApiClient.getPluginConfiguration(common.pluginId)]); if (signal.aborted) return;
        [users, config] = result;
        find('#configurationSummary').replaceChildren(common.linkedUsersBadge(users, config.UserConfig));
        const selected = find('#selectSyncUser').value; common.populateUserList(view, users, '#selectSyncUser');
        if (users.some(user => user.Id === selected)) find('#selectSyncUser').value = selected;
        find('#ManualSyncConfigForm').hidden = false;
        common.status(find('[data-page-status]'), ''); updateProviders();
        busy = false;
        updateForm();
        render();
        if (lastRun && !isFinished(lastRun.state ?? 'Queued')) follow(signal);
        view.querySelectorAll('[data-action="cancel"]').forEach(button => button.addEventListener('click', cancel, { signal }));
        view.querySelectorAll('[data-action="close-preview"]').forEach(button => button.addEventListener('click', () => dialog().close(), { signal }));
        dialog().addEventListener('close', () => { render(); (find('#openPreview').hidden ? find('#previewChanges') : find('#openPreview')).focus(); }, { signal });
        find('#openPreview').addEventListener('click', () => openDialog(signal), { signal });
        find('#previewRunSync').addEventListener('click', () => { if (previewRun) run('sync', previewRun.request); }, { signal });
        find('#selectSyncUser').addEventListener('change', () => { updateProviders(); updateForm(); }, { signal });
        find('#ManualSyncConfigForm').querySelectorAll('input, select').forEach(input => input.addEventListener('change', updateForm, { signal }));
        find('#ManualSyncConfigForm').addEventListener('submit', event => { event.preventDefault(); run('sync'); }, { signal });
        find('#previewChanges').addEventListener('click', () => run('preview'), { signal });
        find('#previewFilters').addEventListener('change', event => {
            if (!previewRun?.preview || event.target.name !== 'previewFilter') return;
            Object.assign(previewRun.preview, { filter: event.target.value, offset: 0 });
            loadRows(signal);
        }, { signal });
        find('#previousPreviewPage').addEventListener('click', () => turnPage(-1, signal), { signal });
        find('#nextPreviewPage').addEventListener('click', () => turnPage(1, signal), { signal });
    }
    function updateProviders() {
        const selected = find('#selectSyncProvider').value;
        find('#selectSyncProvider').replaceChildren();
        links().forEach(key => find('#selectSyncProvider').add(new Option(common.providerName(key), key)));
        if (links().includes(selected)) find('#selectSyncProvider').value = selected;
        const trackers = find('#selectSyncTrackers'), chosen = trackers.value;
        trackers.replaceChildren(new Option('All linked trackers', allTrackers), ...links().map(key => new Option(common.providerName(key), key)));
        trackers.value = links().includes(chosen) ? chosen : allTrackers;
    }
    function updateForm() {
        const inbound = options().syncAction === 'UpdateJellyfin'; find('#inboundOptions').hidden = !inbound; find('#outboundOptions').hidden = inbound;
        let problem = ''; try { buildSyncRequest(options(), links()); } catch (error) { problem = error.message; }
        common.status(find('#formStatus'), problem);
        // With nothing linked there is nothing to preview or run; only the way to link one.
        const linked = links().length > 0;
        find('#settingsLink').hidden = linked;
        find('#previewChanges').hidden = find('#runSync').hidden = !linked;
        find('#previewChanges').disabled = find('#runSync').disabled = busy || !!problem;
        find('#previewRunSync').disabled = busy;
    }
    // Paints the latest run below the form and, for a preview or a sync started from it, in the preview dialog.
    function render() {
        const run = lastRun;
        find('#runPanel').hidden = !run;
        if (!run) return;
        const fromDialog = !!previewRun && (run === previewRun || run.from === previewRun);
        const resultsShown = run === previewRun && !!previewRun.preview?.counts;
        paintRun(find('#pageRun'), run, run.summary);
        paintRun(find('#dialogRun'), run, run.mode === 'sync' ? `Sync · ${run.described}` : '');
        // Once the rows are in, their summary says what the run's status would.
        find('#dialogRun').hidden = !fromDialog || resultsShown;
        find('#previewResults').hidden = find('#previewTabs').hidden = find('#previewPager').hidden = !resultsShown;
        find('#previewRequest').textContent = previewRun?.described ?? '';
        // Run sync belongs to a finished preview; once a sync starts from it, the preview no longer describes what is there.
        const previewDone = run === previewRun && isFinished(run.state ?? 'Queued');
        find('#previewRunSync').hidden = !previewDone;
        find('#previewDialog .doki-dialog-footer').hidden = !previewDone && !resultsShown;
        find('#previewRunSync').disabled = busy;
        find('#openPreview').hidden = !previewRun || dialog().open;
    }
    function paintRun(block, run, summary) {
        const title = block.querySelector('[data-run-summary]');
        title.textContent = summary;
        title.hidden = !summary;
        const bar = block.querySelector('[data-run-bar]'), { value, max } = runProgress(run.state, run.done, run.total);
        bar.dataset.tone = run.tone ?? '';
        bar.toggleAttribute('data-indeterminate', value == null);
        if (value == null) ['aria-valuemin', 'aria-valuemax', 'aria-valuenow'].forEach(name => bar.removeAttribute(name));
        else Object.entries({ 'aria-valuemin': 0, 'aria-valuemax': max, 'aria-valuenow': value }).forEach(([name, number]) => bar.setAttribute(name, String(number)));
        bar.firstElementChild.style.width = value == null ? '' : `${(value / max) * 100}%`;
        common.status(block.querySelector('[data-run-status]'), run.message, run.tone);
        block.querySelector('[data-action="cancel"]').hidden = !run.operationId || isFinished(run.state ?? 'Queued');
    }
    function openDialog(signal) {
        if (!previewRun) return;
        if (!dialog().open) dialog().showModal();
        render();
        find('#previewTitle').focus();
        // Coming back to the page keeps the run but not necessarily its rows; read them again.
        if (previewRun === lastRun && isFinished(previewRun.state ?? 'Queued') && previewRun.operationId) openPreview(previewRun, signal);
    }
    async function cancel() {
        const run = lastRun;
        if (!run?.operationId) return;
        const buttons = view.querySelectorAll('[data-action="cancel"]');
        buttons.forEach(button => { button.disabled = true; });
        try {
            await common.request(`AniDoki/operations/${run.operationId}/cancel`, { method: 'POST' });
            Object.assign(run, { message: 'Cancelling after the current item…', tone: '' });
        } catch (error) {
            // 409: it finished first; the next poll shows the result.
            if (!(error instanceof Response && error.status === 409))
                Object.assign(run, { message: `Could not cancel: ${await common.errorMessage(error)}`, tone: 'error' });
        } finally {
            buttons.forEach(button => { button.disabled = false; });
        }
        render();
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
                Object.assign(run, describeOperation(view), { state: view.state, done: view.done, total: view.total });
                render();
                if (isFinished(view.state)) break;
            } catch (error) {
                if (signal.aborted) return;
                Object.assign(run, { message: `Could not read progress: ${await common.errorMessage(error)}`, tone: 'error' });
                render();
            }
            await new Promise(resolve => setTimeout(resolve, 2000));
        }
        if (signal.aborted) return;
        if (run.mode === 'preview') await openPreview(run, signal);
        if (signal.aborted) return;
        busy = false;
        updateForm();
        render();
    }
    // request: the previewed request, when Run sync is pressed in the preview; otherwise the form's.
    async function run(mode, request) {
        if (busy) return;
        let captured = request;
        if (!captured) {
            try { captured = Object.freeze(buildSyncRequest(options(), links())); }
            catch (error) { common.status(find('#formStatus'), error.message, 'error'); return; }
        }
        const described = describeRequest(captured, users.find(user => user.Id === captured.userId)?.Name ?? captured.userId, common.providerName);
        const summary = mode === 'preview' ? `Preview · ${described}` : described;
        busy = true;
        lastRun = { mode, request: captured, described, summary, message: 'Starting…', tone: '', from: request ? previewRun : null };
        // A preview opens its dialog at once; a sync from the page makes any earlier preview out of date.
        if (mode === 'preview') previewRun = lastRun;
        else if (!request) previewRun = null;
        updateForm();
        render();
        if (mode === 'preview') openDialog(lifetime.signal);
        let started;
        try {
            started = await common.json(`AniDoki/${mode === 'preview' ? 'preview' : 'sync'}?${new URLSearchParams(captured)}`, { method: 'POST' });
        } catch (error) {
            // A run already queued or running for this user and direction answers 409 with its ID; follow that one.
            if (error instanceof Response && error.status === 409) {
                started = await error.json();
                lastRun.summary = `${summary} (already running)`;
            } else {
                busy = false;
                Object.assign(lastRun, { state: 'Failed', message: await common.errorMessage(error), tone: 'error' });
                if (!lifetime.signal.aborted) { updateForm(); render(); }
                return;
            }
        }
        lastRun.operationId = started.operationId;
        follow(lifetime.signal);
    }
    // A finished preview: its counts, then the first page of the most useful view.
    async function openPreview(run, signal) {
        let preview;
        try {
            preview = await common.json(`AniDoki/preview/${run.operationId}`, { signal });
        } catch (error) {
            if (signal.aborted) return;
            const message = error instanceof Response && error.status === 404
                ? 'Another run was already active for this user, so it was followed instead of a preview.'
                : `Could not read the preview: ${await common.errorMessage(error)}`;
            Object.assign(run, { message, tone: 'error' });
            render();
            return;
        }
        if (signal.aborted || previewRun !== run) return;
        // opened: seasons the viewer opened or closed, by group key, kept across filters and pages.
        run.preview ??= { filter: previewState.defaultFilter(preview.rowCounts), offset: 0, opened: new Map() };
        run.preview.counts = preview.rowCounts;
        renderFilters();
        render();
        await loadRows(signal);
    }
    function renderFilters() {
        const { counts, filter } = previewRun.preview;
        find('#previewFilters').replaceChildren(...previewState.filters.map(item => {
            const label = common.element('label', 'doki-preview-tab');
            const input = common.element('input');
            Object.assign(input, { type: 'radio', name: 'previewFilter', value: item.key, checked: item.key === filter });
            label.append(input, common.element('span', '', item.label), common.element('span', 'doki-preview-tab-count', String(previewState.filterCount(item.key, counts))));
            return label;
        }));
        // On a narrow screen the row scrolls; bring the chosen tab into it.
        find('#previewFilters input:checked')?.parentElement.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
    function turnPage(step, signal) {
        const preview = previewRun?.preview;
        if (!preview) return;
        preview.offset = Math.max(0, preview.offset + step * previewState.pageSize);
        loadRows(signal);
    }
    async function loadRows(signal) {
        const run = previewRun, preview = run?.preview;
        if (!preview) return;
        common.status(find('#previewPageStatus'), 'Loading…');
        let page;
        try {
            page = await common.json(previewState.rowsPath(run.operationId, preview.filter, preview.offset), { signal });
        } catch (error) {
            if (!signal.aborted) common.status(find('#previewPageStatus'), await common.errorMessage(error), 'error');
            return;
        }
        if (signal.aborted || previewRun !== run) return;
        renderRows(page.rows, preview);
        common.status(find('#previewPageStatus'), previewState.pageLabel(preview.offset, page.rows.length, page.total));
        // One page needs no buttons; the label alone counts the rows.
        find('#previousPreviewPage').hidden = find('#nextPreviewPage').hidden = !previewState.paged(page.total);
        find('#previousPreviewPage').disabled = preview.offset === 0;
        find('#nextPreviewPage').disabled = preview.offset + page.rows.length >= page.total;
    }
    function renderRows(rows, preview) {
        const table = find('#previewTable');
        table.querySelectorAll('tbody').forEach(body => body.remove());
        if (!rows.length) {
            const body = common.element('tbody'), row = common.element('tr'), cell = common.element('td', '', previewState.emptyLabel(preview.filter));
            cell.colSpan = 4;
            row.append(cell);
            body.append(row);
            table.append(body);
            return;
        }
        for (const group of previewState.groupRows(rows)) {
            const body = common.element('tbody'), heading = common.element('tr', 'doki-preview-group'), title = common.element('th');
            title.scope = 'rowgroup';
            title.colSpan = 4;
            // A season opens and closes from its heading; up-to-date seasons start closed.
            const toggle = common.element('button', 'doki-preview-toggle');
            toggle.type = 'button';
            toggle.append(common.icon('chevron-right'), common.element('span', 'doki-preview-season', group.title), common.element('em', 'doki-preview-summary', group.summary));
            const rowsOf = group.rows.map(row => rowElement(previewState.describeRow(row, common.providerName)));
            const show = open => {
                toggle.setAttribute('aria-expanded', String(open));
                rowsOf.forEach(row => { row.hidden = !open; });
            };
            toggle.addEventListener('click', () => {
                const open = toggle.getAttribute('aria-expanded') !== 'true';
                preview.opened.set(group.key, open);
                show(open);
            });
            show(preview.opened.get(group.key) ?? !group.collapsed);
            title.append(toggle);
            heading.append(title);
            body.append(heading, ...rowsOf);
            table.append(body);
        }
    }
    // Each cell: a value with a muted note beneath. The entry cell leads with the tracker's logo.
    function rowElement(described) {
        const row = common.element('tr');
        const entry = common.element('td', 'doki-preview-entry');
        entry.dataset.label = 'Tracker entry';
        const tracker = common.element('span', 'doki-preview-tracker'), text = common.element('span', 'doki-preview-value');
        text.append(common.element('strong', '', described.tracker), common.element('span', 'doki-preview-note', described.entry));
        tracker.append(common.providerIcon({ key: described.provider }), text);
        entry.append(tracker);
        row.append(entry);
        // Values are bold; a cell with nothing to show (—, No change, Not checked) and the reason are plain.
        const cells = [['Now', described.now, described.nowNote, '', !described.nowQuiet],
            ['Proposed', described.proposed, described.proposedNote, described.proposedTone, !described.proposedQuiet],
            ['Reason', described.why, described.whyNote, described.whyTone, false]];
        for (const [label, value, note, tone, bold] of cells) {
            const cell = common.element('td');
            cell.dataset.label = label;
            if (tone) cell.dataset.tone = tone;
            const content = common.element('span', 'doki-preview-value');
            content.append(common.element(bold ? 'strong' : 'span', bold || label === 'Reason' ? '' : 'doki-preview-quiet', value));
            if (note) content.append(common.element('span', 'doki-preview-note', note));
            cell.append(content);
            row.append(cell);
        }
        return row;
    }
}
