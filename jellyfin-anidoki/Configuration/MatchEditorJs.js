// Draws the match editor and its entry picker, and turns what the user does in them into calls on the page's
// handlers. State lives in match-editor-state.js and in the page; this file only reads it. Every listener is bound
// once per page lifetime, on the container, so redrawing never adds handlers.

const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
};

let serial = 0;
const id = name => `doki-${name}-${++serial}`;

function numberField(label, row, field, value, { placeholder = '', width = 'short' } = {}) {
    const wrapper = element('div', `doki-field doki-field-${width}`);
    const input = element('input', 'doki-input');
    input.id = id(field);
    Object.assign(input, { type: 'text', inputMode: 'numeric', autocomplete: 'off', value, placeholder });
    input.dataset.row = row.key;
    input.dataset.field = field;
    const caption = element('label', '', label);
    caption.htmlFor = input.id;
    wrapper.append(caption, input);
    return wrapper;
}

function entryField(row, label) {
    const wrapper = element('div', 'doki-field doki-field-entry');
    const caption = element('span', 'doki-field-label', label);
    caption.id = id('entry-label');
    const choose = element('button', 'doki-button doki-entry-button', row.target?.title ?? 'Choose an entry');
    choose.type = 'button';
    choose.dataset.action = 'choose-entry';
    choose.dataset.row = row.key;
    choose.setAttribute('aria-describedby', caption.id);
    if (!row.target) choose.dataset.empty = '';
    wrapper.append(caption, choose);
    return wrapper;
}

function removeButton(row, name) {
    const button = element('button', 'doki-button doki-button-quiet', 'Remove');
    button.type = 'button';
    button.dataset.action = 'remove-row';
    button.dataset.row = row.key;
    button.setAttribute('aria-label', `Remove ${name}`);
    return button;
}

function rangeRow(row, index, issues) {
    const name = `range ${index + 1}`;
    const item = element('li', 'doki-match-row');
    item.dataset.row = row.key;
    if (issues.has(row.key)) item.dataset.invalid = '';
    item.setAttribute('aria-label', `Range ${index + 1}`);
    item.append(
        numberField('From episode', row, 'sourceStart', row.sourceStart),
        numberField('To episode', row, 'sourceEnd', row.sourceEnd, { placeholder: 'airing' }),
        entryField(row, 'Entry'),
        numberField('Entry episode', row, 'targetStart', row.targetStart),
        removeButton(row, name));
    return item;
}

function exceptionRow(row, issues) {
    const item = element('li', 'doki-match-row');
    item.dataset.row = row.key;
    if (issues.has(row.key)) item.dataset.invalid = '';
    item.setAttribute('aria-label', row.sourceEpisode ? `Exception for episode ${row.sourceEpisode}` : 'New exception');
    const kind = element('div', 'doki-field doki-field-short');
    const select = element('select', 'doki-input');
    select.id = id('kind');
    select.dataset.row = row.key;
    select.dataset.field = 'kind';
    select.add(new Option('Ignore', 'ignore'));
    select.add(new Option('Send to', 'target'));
    select.value = row.kind;
    const label = element('label', '', 'Action');
    label.htmlFor = select.id;
    kind.append(label, select);
    item.append(numberField('Episode', row, 'sourceEpisode', row.sourceEpisode), kind);
    if (row.kind === 'target') item.append(entryField(row, 'Entry'), numberField('Entry episode', row, 'targetEpisode', row.targetEpisode));
    item.append(removeButton(row, `the exception for episode ${row.sourceEpisode || 'new'}`));
    return item;
}

// Redraws the rows. Typing does not redraw (see bindEditor), so this runs only when rows are added, removed or change shape.
export function renderEditor(container, state, issues = []) {
    const invalid = new Set(issues.map(issue => issue.row).filter(Boolean));
    const ranges = element('ol', 'doki-match-rows');
    state.draft.ranges.forEach((row, index) => ranges.append(rangeRow(row, index, invalid)));
    const exceptions = element('ol', 'doki-match-rows');
    state.draft.exceptions.forEach(row => exceptions.append(exceptionRow(row, invalid)));
    const add = (action, text) => {
        const button = element('button', 'doki-button', text);
        button.type = 'button';
        button.dataset.action = action;
        return button;
    };
    const note = element('div', 'doki-field doki-match-note');
    const noteInput = element('textarea', 'doki-input');
    noteInput.id = id('note');
    noteInput.rows = 2;
    noteInput.value = state.draft.note;
    noteInput.dataset.field = 'note';
    const noteLabel = element('label', '', 'Note');
    noteLabel.htmlFor = noteInput.id;
    note.append(noteLabel, noteInput);
    container.classList.add('doki-match-editor');
    container.replaceChildren(
        element('h4', '', 'Ranges'),
        state.draft.ranges.length ? ranges : element('p', 'doki-help', 'No ranges. Add one to send a run of episodes to an entry.'),
        add('add-range', 'Add range'),
        element('h4', '', 'Exceptions'),
        state.draft.exceptions.length ? exceptions : element('p', 'doki-help', 'No exceptions. Add one to send a single episode elsewhere, or to ignore it.'),
        add('add-exception', 'Add exception'),
        note);
}

// Marks rows with issues and lists the issues, without redrawing the inputs.
export function renderIssues(container, list, issues) {
    const invalid = new Set(issues.map(issue => issue.row).filter(Boolean));
    container.querySelectorAll('.doki-match-row').forEach(row => {
        if (invalid.has(row.dataset.row)) row.dataset.invalid = '';
        else delete row.dataset.invalid;
    });
    list.replaceChildren(...issues.map(issue => element('li', '', issue.message)));
    list.hidden = issues.length === 0;
}

// The entry picker: a search box and its results, for the row being edited. picker: { row, query, results, message, busy }.
export function renderPicker(container, picker, providerName) {
    container.hidden = !picker;
    if (!picker) return container.replaceChildren();
    const heading = element('h4', '', `Choose a ${providerName} entry`);
    heading.id = id('picker');
    const form = element('form', 'doki-actions');
    form.dataset.action = 'search-entries';
    form.setAttribute('role', 'search');
    const input = element('input', 'doki-input');
    input.id = id('query');
    Object.assign(input, { type: 'search', value: picker.query, placeholder: 'Title' });
    input.dataset.field = 'picker-query';
    const label = element('label', 'doki-sr-only', 'Title to search for');
    label.htmlFor = input.id;
    const search = element('button', 'doki-button doki-button-primary', picker.busy ? 'Searching…' : 'Search');
    search.type = 'submit';
    search.disabled = picker.busy;
    const cancel = element('button', 'doki-button', 'Cancel');
    cancel.type = 'button';
    cancel.dataset.action = 'close-picker';
    form.append(label, input, search, cancel);
    const results = element('ul', 'doki-picker-results');
    results.setAttribute('aria-labelledby', heading.id);
    (picker.results ?? []).forEach((candidate, index) => {
        const choose = element('button', 'doki-button doki-picker-result');
        choose.type = 'button';
        choose.dataset.action = 'pick-entry';
        choose.dataset.index = String(index);
        const details = [candidate.english, candidate.totalEpisodes ? `${candidate.totalEpisodes} episodes` : 'length unknown', `ID ${candidate.entry.id}`].filter(Boolean);
        choose.append(element('strong', '', candidate.entry.title || `ID ${candidate.entry.id}`), element('span', 'doki-help', details.join(' · ')));
        const item = element('li');
        item.append(choose);
        results.append(item);
    });
    const message = element('p', 'doki-status', picker.message ?? '');
    message.setAttribute('role', 'status');
    container.replaceChildren(heading, form, message, results);
}

// handlers: change(row, field, value) for typing, which redraws nothing; edit(action, row) for structure; picker
// actions search(query), pick(index) and close(). Bound once per lifetime; signal removes them all.
export function bindEditor(container, picker, handlers, signal) {
    container.addEventListener('input', event => {
        const field = event.target.dataset?.field;
        if (!field || event.target.tagName === 'SELECT') return;
        handlers.change(event.target.dataset.row ?? null, field, event.target.value);
    }, { signal });
    container.addEventListener('change', event => {
        if (event.target.tagName === 'SELECT' && event.target.dataset.field) handlers.edit('kind', event.target.dataset.row, event.target.value);
    }, { signal });
    container.addEventListener('click', event => {
        const button = event.target.closest?.('[data-action]');
        if (!button || !container.contains(button)) return;
        handlers.edit(button.dataset.action, button.dataset.row ?? null);
    }, { signal });
    picker.addEventListener('submit', event => {
        event.preventDefault();
        handlers.search(picker.querySelector('[data-field="picker-query"]')?.value ?? '');
    }, { signal });
    picker.addEventListener('click', event => {
        const button = event.target.closest?.('[data-action]');
        if (button?.dataset.action === 'pick-entry') handlers.pick(Number(button.dataset.index));
        else if (button?.dataset.action === 'close-picker') handlers.close();
    }, { signal });
    // Escape closes the picker only, not the dialog around it.
    picker.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        event.preventDefault();
        event.stopPropagation();
        handlers.close();
    }, { signal });
}

// The season dialog. Escape asks close(), which steps back or may refuse while there are unsaved changes. Chrome only
// lets a page cancel the dialog's own Escape after a user action, so Escape is taken on keydown, before the dialog acts;
// cancel still covers other close requests, such as Android's back gesture. closed() tidies up after any close the page
// did not ask for.
export function bindDialog(dialog, close, closed, signal) {
    let escaped = false;
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return;
        event.preventDefault();
        escaped = true;
        setTimeout(() => { escaped = false; });
        close();
    }, { signal });
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        if (!escaped) close();
    }, { signal });
    dialog.addEventListener('close', () => closed(), { signal });
}

// A saved mapping in words, for history and a user's correction; nothing in it can be edited.
export function describeMapping(mapping) {
    const lines = (mapping?.ranges ?? []).map(range => `Episodes ${range.sourceStart}–${range.sourceEnd ?? 'on'} → ${range.target.title}, from episode ${range.targetStart}`);
    for (const exception of mapping?.exceptions ?? [])
        lines.push(exception.kind === 'target'
            ? `Episode ${exception.sourceEpisode} → ${exception.target.title}, episode ${exception.targetEpisode}`
            : `Episode ${exception.sourceEpisode} ignored`);
    return lines.length ? lines : ['Nothing (deleted)'];
}

// A fetch error as the API answers it. Jellyfin leaves out null properties, so every field may be missing.
export async function problem(error) {
    if (error instanceof Response) {
        try { return { status: error.status, body: await error.clone().json() }; } catch { return { status: error.status, body: null }; }
    }
    return { status: 0, body: null };
}

// One click handler for everything with a data-action outside the editor and its picker, bound once per lifetime.
export function bindPageActions(root, handlers, signal) {
    root.addEventListener('click', event => {
        const button = event.target.closest?.('[data-action]');
        if (!button || button.closest('#matchEditor, #entryPicker')) return;
        const run = handlers[button.dataset.action];
        if (run) { event.preventDefault(); run(button); }
    }, { signal });
}

// The episode map's rows. describe is match-editor-state.js's describeRow.
export function renderEpisodeMap(tbody, rows, describe) {
    tbody.replaceChildren(...rows.map(row => {
        const line = describe(row);
        const tr = element('tr');
        const cell = element('td', line.tone === 'error' ? 'doki-status' : line.tone === 'muted' ? 'doki-muted' : '', line.text);
        if (line.tone) cell.dataset.tone = line.tone;
        tr.append(element('td', 'doki-mono', String(row.sourceEpisode)), cell);
        return tr;
    }));
}

// One entry picker per editor: which row it serves, its results, and its searches. search(query) resolves to the
// API's EntrySearchContract; errorMessage is common.errorMessage.
export function createEntryPicker(container, { search, providerName, errorMessage }) {
    let picker = null;
    const draw = () => renderPicker(container, picker, providerName());
    return {
        get row() { return picker?.row ?? null; },
        result: index => picker?.results?.[index] ?? null,
        open(row, query) {
            picker = { row, query, results: [], message: '', busy: false };
            draw();
            container.querySelector('input')?.focus();
        },
        close() {
            picker = null;
            renderPicker(container, null, '');
        },
        async search(query, signal) {
            if (!picker) return;
            picker = { ...picker, query, busy: true, message: 'Searching…' };
            draw();
            let result;
            try {
                result = await search(query);
            } catch (error) {
                if (signal.aborted || !picker) return;
                picker = { ...picker, busy: false, message: await errorMessage(error) };
                return draw();
            }
            if (signal.aborted || !picker) return;
            const results = result?.entries ?? [];
            picker = { ...picker, busy: false, results,
                message: results.length ? `${results.length} found.${result.borrowedLink ? ' Read through another user’s link.' : ''}` : 'Nothing found. Try another title.' };
            draw();
            container.querySelector('.doki-picker-result')?.focus();
        }
    };
}
