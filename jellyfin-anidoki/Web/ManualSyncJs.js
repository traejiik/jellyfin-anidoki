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
// The way a run goes, named by its trackers: "Jellyfin → All trackers", "Jellyfin → AniList" or "AniList → Jellyfin".
export function describeRequest(request, providerName) {
    if (request.syncAction === 'UpdateJellyfin') return `${providerName(request.provider)} → Jellyfin`;
    return `Jellyfin → ${request.provider === allTrackers ? 'All trackers' : providerName(request.provider)}`;
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
// An apply of preview rows counts the rows it was given: changes outbound, seasons inbound.
const applyUnits = { SyncToTrackers: 'changes', SyncFromTrackers: 'seasons' };
// Turns an operation view from AniDoki/operations/{id} into one status line and a tone.
export function describeOperation(view) {
    const apply = !!view.previewOf && !!applyUnits[view.kind];
    if (view.state === 'Queued') return { message: 'Waiting to start…', tone: '' };
    if (view.state === 'Running') {
        const unit = (apply ? applyUnits : units)[view.kind] ?? 'items';
        const message = view.total == null ? 'Running…' : `Running: ${view.done} of ${view.total} ${unit} checked…`;
        return { message, tone: '' };
    }
    const counts = countLabels.filter(([key]) => view.counts?.[key]).map(([key, label]) => `${view.counts[key]} ${label}`).join(' · ');
    const tone = ['Succeeded', 'NoChange'].includes(view.state) ? 'success' : ['Failed', 'Unconfirmed', 'Interrupted'].includes(view.state) ? 'error' : '';
    // An apply's message already counts each row's result in the preview's words, so it stands alone.
    if (apply) return { message: view.message || headings[view.state] || view.state, tone };
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
    const viewDocument = view.ownerDocument;
    // lastRun: the latest run of any kind. previewRun: the preview the dialog shows, until a sync from the page replaces it;
    // previewRun.apply is the apply of its rows, once there is one.
    let lifetime, common, previewState, users = [], config, busy = false, lastRun, previewRun;
    const find = selector => view.querySelector(selector);
    const links = () => (config?.UserConfig?.find(user => user.UserId === find('#selectSyncUser').value)?.UserApiAuth ?? []).map(auth => auth.Name);
    const options = () => ({ userId: find('#selectSyncUser').value, provider: find('#selectSyncProvider').value, trackers: find('#selectSyncTrackers').value,
        syncAction: find('input[name="syncAction"]:checked').value, completed: find('#completedStatus').checked, watching: find('#watchingStatus').checked });
    const dialog = () => find('#previewDialog');
    const applying = run => !!run && !isFinished(run.state ?? 'Queued');
    // The counts the tabs show, with the applied rows as their own.
    const tabCounts = preview => ({ ...preview.counts, Applied: preview.appliedCount ?? 0 });
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
        common.ensureStyles(); common.followThemeAccent(find('.anidoki'), signal); common.setTabs(common.TabManualSync, common.getTabs, view);
        find('#previousPreviewPage').replaceChildren(common.icon('chevron-left'));
        find('#nextPreviewPage').replaceChildren(common.icon('chevron-right'));
        common.selectPluginSidebar('AniDoki_ManualSync', signal, view.ownerDocument);
        // A modal dialog would stay on top of the next page.
        signal.addEventListener('abort', () => {
            if (find('#confirmApply').open) find('#confirmApply').close();
            if (dialog().open) dialog().close();
        }, { once: true });
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
        if (applying(lastRun)) follow(signal);
        view.querySelectorAll('[data-action="cancel"]').forEach(button => button.addEventListener('click', cancel, { signal }));
        view.querySelectorAll('[data-action="close-preview"]').forEach(button => button.addEventListener('click', () => dialog().close(), { signal }));
        view.querySelectorAll('[data-action="close-confirm"]').forEach(button => button.addEventListener('click', () => find('#confirmApply').close(), { signal }));
        dialog().addEventListener('close', () => { render(); (find('#openPreview').hidden ? find('#previewChanges') : find('#openPreview')).focus(); }, { signal });
        find('#confirmApply').addEventListener('close', () => { if (dialog().open) find('#applySelected').focus(); }, { signal });
        find('#openPreview').addEventListener('click', () => openDialog(signal), { signal });
        find('#selectSyncUser').addEventListener('change', () => { updateProviders(); updateForm(); }, { signal });
        find('#ManualSyncConfigForm').querySelectorAll('input, select').forEach(input => input.addEventListener('change', updateForm, { signal }));
        // Enter in the form previews: nothing is written without a choice.
        find('#ManualSyncConfigForm').addEventListener('submit', event => { event.preventDefault(); run('preview'); }, { signal });
        find('#runSync').addEventListener('click', () => run('sync'), { signal });
        find('#previewFilters').addEventListener('change', event => {
            if (!previewRun?.preview || event.target.name !== 'previewFilter') return;
            Object.assign(previewRun.preview, { filter: event.target.value, offset: 0 });
            loadRows(signal);
        }, { signal });
        find('#previousPreviewPage').addEventListener('click', () => turnPage(-1, signal), { signal });
        find('#nextPreviewPage').addEventListener('click', () => turnPage(1, signal), { signal });
        find('#previewSelectAll').addEventListener('change', toggleAll, { signal });
        find('#previewTable').addEventListener('change', event => toggleRow(event.target), { signal });
        find('#applySelected').addEventListener('click', () => requestApply(signal), { signal });
        find('#confirmApplyButton').addEventListener('click', () => { find('#confirmApply').close(); startApply(); }, { signal });
        find('#refreshPreview').addEventListener('click', () => { if (previewRun) run('preview', previewRun.request); }, { signal });
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
    }
    // Paints the latest run below the form and, for a preview or an apply of it, in the preview dialog.
    function render() {
        const run = lastRun;
        find('#runPanel').hidden = !run;
        if (!run) return;
        const fromDialog = !!previewRun && (run === previewRun || run.from === previewRun);
        const resultsShown = !!previewRun?.preview?.counts;
        paintRun(find('#pageRun'), run, true);
        paintRun(find('#dialogRun'), run, false);
        // Once the rows are in, their summary says what the run's status would.
        find('#dialogRun').hidden = !fromDialog || resultsShown;
        find('#previewResults').hidden = find('#previewTabs').hidden = find('#previewPager').hidden = !resultsShown;
        if (previewRun) paintSummary(find('#previewRequest'), previewRun);
        else find('#previewRequest').replaceChildren();
        paintApply(previewRun?.apply);
        renderNotice();
        renderFooter();
        find('#openPreview').hidden = !previewRun || dialog().open;
    }
    function paintRun(block, run, withSummary) {
        const title = block.querySelector('[data-run-summary]');
        if (withSummary) paintSummary(title, run);
        title.hidden = !withSummary;
        paintBar(block.querySelector('[data-run-bar]'), run);
        common.status(block.querySelector('[data-run-status]'), run.message, run.tone);
        block.querySelector('[data-action="cancel"]').hidden = !run.operationId || isFinished(run.state ?? 'Queued');
    }
    // Whose run it is, in its own section, then which way it goes.
    function paintSummary(node, run) {
        const user = common.element('span', 'doki-run-user');
        user.append(common.element('strong', '', 'User:'), ` ${run.userName}`);
        node.replaceChildren(user, common.element('span', 'doki-run-direction', `${run.described}${run.note ?? ''}`));
    }
    function paintBar(bar, run) {
        const { value, max } = runProgress(run.state, run.done, run.total);
        bar.dataset.tone = run.tone ?? '';
        bar.toggleAttribute('data-indeterminate', value == null);
        if (value == null) ['aria-valuemin', 'aria-valuemax', 'aria-valuenow'].forEach(name => bar.removeAttribute(name));
        else Object.entries({ 'aria-valuemin': 0, 'aria-valuemax': max, 'aria-valuenow': value }).forEach(([name, number]) => bar.setAttribute(name, String(number)));
        bar.firstElementChild.style.width = value == null ? '' : `${(value / max) * 100}%`;
    }
    // The apply's progress sits above its rows while it runs: one line over a thin bar, with Stop beside it.
    function paintApply(run) {
        const block = find('#applyRun');
        block.hidden = !applying(run);
        if (block.hidden) return;
        const line = run.state === 'Queued' || !run.total ? 'Applying…' : `Applying ${Math.min(run.done ?? 0, run.total)} of ${run.total}…`;
        common.status(block.querySelector('[data-run-status]'), run.message?.startsWith('Stopping') || run.tone === 'error' ? run.message : line, run.tone);
        paintBar(block.querySelector('[data-run-bar]'), run);
        block.querySelector('[data-action="cancel"]').hidden = !run.operationId;
    }
    // A finished preview that can no longer be applied, and was not, says so above its rows.
    function renderNotice() {
        const preview = previewRun?.preview, notice = find('#previewNotice');
        notice.hidden = !preview?.counts || preview.applicable || !!previewRun.apply;
        if (!notice.hidden) notice.textContent = previewState.outOfDateLabel(preview.state);
    }
    // The footer holds one action: apply the selection, or refresh once the preview cannot be applied.
    function renderFooter() {
        const preview = previewRun?.preview, apply = previewRun?.apply;
        const footer = find('#previewDialog .doki-dialog-footer');
        footer.hidden = !preview?.counts;
        let text = '', tone = '', applyButton = false, refresh = false;
        if (applying(apply)) text = 'You can close this; the apply keeps running.';
        else if (apply) [text, tone, refresh] = [apply.message ?? '', apply.tone ?? '', true];
        else if (preview?.applicable && preview.selectable?.length === 0) text = 'Nothing here can be applied.';
        else if (preview?.applicable && preview.selectable) {
            const summary = previewState.selectionSummary(preview.selected, preview.selectable, preview.direction);
            [text, applyButton] = [summary.status, true];
            find('#applySelected').textContent = summary.button;
            find('#applySelected').disabled = busy || summary.count === 0;
        } else if (preview?.counts) [text, tone, refresh] = [preview.error ?? '', preview.error ? 'error' : '', true];
        common.status(find('#previewFooterStatus'), text, tone);
        find('#applySelected').hidden = !applyButton;
        find('#refreshPreview').hidden = !refresh;
        find('#refreshPreview').disabled = busy;
    }
    function openDialog(signal) {
        if (!previewRun) return;
        common.showThemedDialog(dialog());
        render();
        find('#previewTitle').focus();
        // Coming back to the page keeps the run but not necessarily its rows or whether it can still be applied; read them again.
        if (isFinished(previewRun.state ?? 'Queued') && previewRun.operationId) openPreview(previewRun, signal);
    }
    async function cancel() {
        const run = lastRun;
        if (!run?.operationId) return;
        const buttons = view.querySelectorAll('[data-action="cancel"]');
        buttons.forEach(button => { button.disabled = true; });
        try {
            await common.request(`AniDoki/operations/${run.operationId}/cancel`, { method: 'POST' });
            Object.assign(run, { message: 'Stopping after the current item…', tone: '' });
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
                const finished = isFinished(view.state);
                if (finished && run.mode !== 'preview') {
                    const normalizeId = id => id?.replaceAll('-', '').toLowerCase();
                    if (normalizeId(run.request.userId) === normalizeId(globalThis.ApiClient?.getCurrentUserId?.()))
                        viewDocument.dispatchEvent(new Event('anidoki:progress-changed'));
                }
                // An apply's rows fill in their results as it goes.
                if (run.mode === 'apply' && run === previewRun?.apply) await loadRows(signal);
                if (finished) break;
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
    // request: the previewed request, when a preview is refreshed from its dialog; otherwise the form's.
    async function run(mode, request) {
        if (busy) return;
        let captured = request;
        if (!captured) {
            try { captured = Object.freeze(buildSyncRequest(options(), links())); }
            catch (error) { common.status(find('#formStatus'), error.message, 'error'); return; }
        }
        const described = describeRequest(captured, common.providerName);
        const userName = users.find(user => user.Id === captured.userId)?.Name ?? captured.userId;
        busy = true;
        lastRun = { mode, request: captured, described, userName, message: 'Starting…', tone: '' };
        // A preview opens its dialog at once; a sync from the page makes any earlier preview out of date.
        if (mode === 'preview') previewRun = lastRun;
        else previewRun = null;
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
                lastRun.note = ' (already running)';
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
    // A finished preview: its counts, whether it can be applied (and what starts selected), or the apply it already had.
    async function openPreview(run, signal) {
        let read;
        try {
            read = await common.json(`AniDoki/preview/${run.operationId}`, { signal });
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
        const preview = run.preview ??= { offset: 0, opened: new Map(), direction: run.request.syncAction === 'UpdateJellyfin' ? 'FromTrackers' : 'ToTrackers' };
        Object.assign(preview, { counts: read.rowCounts, applicable: read.applicable, state: read.operation.state, error: null });
        if (preview.applicable && !preview.selectable) {
            try {
                preview.selectable = await common.json(`AniDoki/preview/${run.operationId}/selectable`, { signal });
                preview.selected = previewState.defaultSelection(preview.selectable);
            } catch (error) {
                if (signal.aborted) return;
                Object.assign(preview, { applicable: false, error: `Could not read which rows can be applied: ${await common.errorMessage(error)}` });
            }
        }
        if (read.appliedOperationId && run.apply?.operationId !== read.appliedOperationId) await adoptApply(run, read.appliedOperationId, signal);
        if (signal.aborted || previewRun !== run) return;
        if (run.apply) preview.appliedCount = (await common.json(previewState.rowsPath(run.operationId, 'applied', 0), { signal }).catch(() => null))?.total ?? preview.appliedCount;
        if (signal.aborted || previewRun !== run) return;
        preview.filter ??= previewState.defaultFilter(tabCounts(preview), { direction: preview.direction, applied: !!run.apply });
        renderFilters();
        render();
        await loadRows(signal);
    }
    // The preview was applied by a run this page did not start (or no longer holds): show it, and follow it while it runs.
    async function adoptApply(run, applyId, signal) {
        let read;
        try { read = await common.json(`AniDoki/operations/${applyId}`, { signal }); } catch { return; }
        if (signal.aborted) return;
        run.apply = { mode: 'apply', from: run, operationId: applyId, request: run.request, described: run.described,
            userName: run.userName, ...describeOperation(read),
            state: read.state, done: read.done, total: read.total };
        run.preview.filter = 'applied';
        if (applying(run.apply) && !busy) {
            lastRun = run.apply;
            follow(signal);
        }
    }
    function renderFilters() {
        const preview = previewRun.preview;
        find('#previewFilters').replaceChildren(...previewState.filtersFor(preview.direction, !!previewRun.apply).map(item => {
            const label = common.element('label', 'doki-preview-tab');
            const input = common.element('input');
            Object.assign(input, { type: 'radio', name: 'previewFilter', value: item.key, checked: item.key === preview.filter });
            label.append(input, common.element('span', '', item.label),
                common.element('span', 'doki-preview-tab-count', String(previewState.filterCount(item.key, tabCounts(preview)))));
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
        if (!preview?.filter) return;
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
    // Rows can be ticked only while the preview can be applied and nothing has been yet.
    const choosing = preview => !!preview?.applicable && !!preview.selectable && !previewRun?.apply;
    function renderSelectAll() {
        const preview = previewRun?.preview, box = find('#previewSelectAll');
        const state = choosing(preview) ? previewState.filterSelection(preview.selected, preview.selectable, preview.filter) : 'empty';
        box.hidden = state === 'empty';
        box.checked = state === 'all';
        box.indeterminate = state === 'some';
    }
    function toggleAll() {
        const preview = previewRun?.preview;
        if (!choosing(preview)) return;
        const on = previewState.filterSelection(preview.selected, preview.selectable, preview.filter) !== 'all';
        preview.selected = previewState.selectAll(preview.selected, preview.selectable, preview.filter, on);
        find('#previewTable').querySelectorAll('input[data-seq]').forEach(box => {
            box.checked = preview.selected.has(Number(box.dataset.seq));
            box.closest('tr').dataset.selected = String(box.checked);
        });
        renderSelectAll();
        renderFooter();
    }
    function toggleRow(box) {
        const preview = previewRun?.preview;
        if (!box.matches('input[data-seq]') || !choosing(preview)) return;
        if (box.checked) preview.selected.add(Number(box.dataset.seq));
        else preview.selected.delete(Number(box.dataset.seq));
        box.closest('tr').dataset.selected = String(box.checked);
        renderSelectAll();
        renderFooter();
    }
    function renderRows(rows, preview) {
        const table = find('#previewTable');
        table.querySelectorAll('tbody').forEach(body => body.remove());
        find('#previewReasonHeading').textContent = preview.filter === 'applied' ? 'Result' : 'Reason';
        renderSelectAll();
        if (!rows.length) {
            const body = common.element('tbody'), row = common.element('tr'), cell = common.element('td', '', previewState.emptyLabel(preview.filter));
            cell.colSpan = 5;
            row.append(cell);
            body.append(row);
            table.append(body);
            return;
        }
        for (const group of previewState.groupRows(rows)) {
            const body = common.element('tbody'), heading = common.element('tr', 'doki-preview-group'), title = common.element('th');
            title.scope = 'rowgroup';
            title.colSpan = 5;
            // A season opens and closes from its heading; up-to-date seasons start closed.
            const toggle = common.element('button', 'doki-preview-toggle');
            toggle.type = 'button';
            toggle.append(common.icon('chevron-right'), common.element('span', 'doki-preview-season', group.title), common.element('em', 'doki-preview-summary', group.summary));
            const rowsOf = group.rows.map(row => rowElement(row, previewState.describeRow(row, common.providerName), preview));
            const show = open => {
                toggle.setAttribute('aria-expanded', String(open));
                rowsOf.forEach(row => { row.hidden = !open; });
            };
            toggle.addEventListener('click', () => {
                const open = toggle.getAttribute('aria-expanded') !== 'true';
                preview.opened.set(group.key, open);
                show(open);
            });
            // The applied tab shows every season open: its rows are the results.
            show(preview.opened.get(group.key) ?? (preview.filter === 'applied' || !group.collapsed));
            title.append(toggle);
            heading.append(title);
            body.append(heading, ...rowsOf);
            table.append(body);
        }
    }
    // Each cell: a value with a muted note beneath. The first cell ticks the row, or shows its result once applied.
    function rowElement(row, described, preview) {
        const element = common.element('tr');
        const result = row.appliedOperationId && previewRun?.apply
            ? previewState.resultLabel(row.outcome, common.providerName, row.provider, preview.direction, applying(previewRun.apply))
            : null;
        const select = common.element('td', 'doki-select-cell');
        if (result) {
            const icon = common.element('span', 'doki-outcome');
            icon.dataset.icon = result.icon;
            icon.setAttribute('aria-hidden', 'true');
            select.dataset.tone = result.tone;
            select.append(icon);
        } else if (choosing(preview) && row.selectable) {
            const box = common.element('input', 'doki-choice-checkbox');
            Object.assign(box, { type: 'checkbox', checked: preview.selected.has(row.seq) });
            box.dataset.seq = String(row.seq);
            box.setAttribute('aria-label', `Apply ${described.tracker}, ${described.entry}`);
            element.dataset.selected = String(box.checked);
            select.append(box);
        }
        if (row.kind === 'Reduction') element.dataset.reduction = 'true';
        const entry = common.element('td', 'doki-preview-entry');
        entry.dataset.label = 'Tracker entry';
        const tracker = common.element('span', 'doki-preview-tracker'), text = common.element('span', 'doki-preview-value');
        text.append(common.element('strong', '', described.tracker), common.element('span', 'doki-preview-note', described.entry));
        tracker.append(common.providerIcon({ key: described.provider }), text);
        entry.append(tracker);
        element.append(select, entry);
        // Values are bold; a cell with nothing to show (—, No change, Not checked) and the reason are plain. A result replaces
        // the reason, in its tone and with nothing beneath.
        const cells = [['Now', described.now, described.nowNote, '', !described.nowQuiet],
            ['Proposed', described.proposed, described.proposedNote, described.proposedTone, !described.proposedQuiet],
            result ? ['Result', result.text, '', result.tone, false] : ['Reason', described.why, described.whyNote, described.whyTone, false]];
        for (const [label, value, note, tone, bold] of cells) {
            const cell = common.element('td');
            cell.dataset.label = label;
            if (tone) cell.dataset.tone = tone;
            const content = common.element('span', 'doki-preview-value');
            const quiet = label === 'Result' ? 'doki-result' : bold || label === 'Reason' ? '' : 'doki-preview-quiet';
            content.append(common.element(bold ? 'strong' : 'span', quiet, value));
            if (note) content.append(common.element('span', 'doki-preview-note', note));
            cell.append(content);
            element.append(cell);
        }
        return element;
    }
    // Apply asks first only when a ticked row would lower progress on a tracker.
    async function requestApply(signal) {
        const preview = previewRun?.preview;
        if (!choosing(preview) || busy) return;
        const summary = previewState.selectionSummary(preview.selected, preview.selectable, preview.direction);
        if (!summary.count) return;
        if (!summary.needsConfirm) {
            startApply();
            return;
        }
        const count = summary.reductions.length;
        find('#confirmTitle').textContent = `${summary.button}?`;
        // When every selected row is a reduction, "1 of them" would read oddly.
        find('#confirmLead').textContent = count === summary.count
            ? (count === 1 ? 'It lowers progress:' : 'They all lower progress:')
            : (count === 1 ? '1 of them lowers progress:' : `${count} of them lower progress:`);
        find('#confirmApplyButton').textContent = summary.button;
        const list = find('#confirmList');
        list.replaceChildren(common.element('li', 'doki-help', 'Loading…'));
        common.showThemedDialog(find('#confirmApply'));
        find('#confirmApply').querySelector('[data-action="close-confirm"]').focus();
        try {
            const page = await common.json(previewState.rowsPath(previewRun.operationId, 'reductions', 0), { signal });
            if (signal.aborted) return;
            list.replaceChildren(...page.rows.filter(row => summary.reductions.includes(row.seq)).map(row => {
                const described = previewState.describeRow(row, common.providerName);
                const item = common.element('li', 'doki-confirm-item'), text = common.element('span', 'doki-preview-value');
                text.append(common.element('strong', '', `${described.tracker} · ${described.entry}`),
                    common.element('span', 'doki-confirm-change', `${described.now} → ${described.proposed.replace(/^→ /, '')}`),
                    common.element('span', 'doki-preview-note', row.sourceTitle));
                item.append(common.providerIcon({ key: described.provider }), text);
                return item;
            }));
        } catch (error) {
            if (!signal.aborted) list.replaceChildren(common.element('li', 'doki-help', `Could not list them: ${await common.errorMessage(error)}`));
        }
    }
    async function startApply() {
        const run = previewRun, preview = run?.preview;
        if (!choosing(preview) || busy) return;
        const summary = previewState.selectionSummary(preview.selected, preview.selectable, preview.direction);
        const rows = preview.selectable.filter(row => preview.selected.has(row.seq)).map(row => row.seq);
        if (!rows.length) return;
        const before = { lastRun, filter: preview.filter, offset: preview.offset };
        busy = true;
        run.apply = { mode: 'apply', from: run, request: run.request, described: run.described, userName: run.userName,
            message: 'Starting…', tone: '', state: 'Queued', total: summary.count, done: 0 };
        lastRun = run.apply;
        Object.assign(preview, { filter: 'applied', offset: 0, appliedCount: rows.length });
        updateForm();
        renderFilters();
        render();
        // The selection is spent: until the applied rows load, the old ones can no longer be ticked.
        renderSelectAll();
        find('#previewTable').querySelectorAll('input[data-seq]').forEach(box => { box.disabled = true; });
        let started;
        try {
            started = await common.json(`AniDoki/preview/${run.operationId}/apply`, { method: 'POST', body: { rows } });
        } catch (error) {
            // Nothing was applied: put the preview back as it was, saying why.
            let message = await common.errorMessage(error.clone?.() ?? error);
            if (error instanceof Response && error.status === 409) {
                const body = await error.json().catch(() => null);
                // Another sync for this user holds the slot; the preview itself can still be applied afterwards.
                if (body?.operationId) message = 'Another sync is running for this user. Apply again once it has finished.';
                else preview.applicable = false;
            }
            run.apply = null;
            lastRun = before.lastRun;
            Object.assign(preview, { filter: before.filter, offset: before.offset, appliedCount: 0 });
            busy = false;
            if (lifetime.signal.aborted) return;
            updateForm();
            renderFilters();
            render();
            if (preview.applicable) common.status(find('#previewFooterStatus'), message, 'error');
            await loadRows(lifetime.signal);
            return;
        }
        run.apply.operationId = started.operationId;
        await loadRows(lifetime.signal);
        follow(lifetime.signal);
    }
}
