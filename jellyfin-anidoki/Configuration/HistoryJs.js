// The History table on the user's page and the admin's History section, with its loading, failed, storage and empty
// states and pages of 10. Every state keeps the heading row, so the panel does not jump when rows arrive.
import { historyMessages, historyRow, pageRange } from './history-state.js';

export const pageSize = 10;

/**
 * Renders into container. load(offset, limit) resolves to { entries, more } or rejects with the failed Response.
 * own: the signed-in user's history (wording "your"). userName(id): adds a User column, for the admin's all-users table.
 */
export default function renderHistory(container, { load, own = true, userName }, common, signal) {
    const columns = userName ? ['Episode', 'User', 'Trigger', 'When', 'Result'] : ['Episode', 'Trigger', 'When', 'Result'];
    const table = common.element('table', 'doki-update-table');
    if (userName) table.classList.add('doki-update-table-users');
    const head = common.element('thead'), headRow = common.element('tr');
    for (const name of columns) { const cell = common.element('th', `doki-update-${name.toLowerCase()}`, name); cell.scope = 'col'; headRow.append(cell); }
    head.append(headRow);
    const body = common.element('tbody');
    table.append(head, body);
    const shell = common.element('div', 'doki-table-shell'); shell.append(table);
    const spoken = common.element('p', 'doki-sr-only'); spoken.setAttribute('role', 'status');
    const pager = common.element('nav', 'doki-pager'); pager.setAttribute('aria-label', 'History pages'); pager.hidden = true;
    const previous = common.element('button', 'doki-button', 'Previous'); previous.type = 'button';
    const range = common.element('span', 'doki-pager-range');
    const next = common.element('button', 'doki-button', 'Next'); next.type = 'button';
    pager.append(previous, range, next);
    container.classList.add('doki-history');
    container.replaceChildren(shell, pager, spoken);
    let offset = 0;

    const message = (text, tone, retry) => {
        const row = common.element('tr'), cell = common.element('td', 'doki-update-message'); cell.colSpan = columns.length;
        cell.dataset.tone = tone;
        const words = common.element('p');
        if (tone === 'attention') words.append(common.icon('triangle'));
        words.append(text); cell.append(words);
        if (retry) { const again = common.element('button', 'doki-button', 'Try again'); again.type = 'button'; again.addEventListener('click', retry, { signal }); cell.append(again); }
        row.append(cell); body.replaceChildren(row);
    };
    const skeleton = rows => {
        table.setAttribute('aria-busy', 'true');
        body.replaceChildren(...Array.from({ length: rows }, () => {
            const row = common.element('tr', 'doki-update-loading'); row.setAttribute('aria-hidden', 'true');
            for (const _ of columns) { const cell = common.element('td'); cell.append(common.element('span', 'doki-update-skeleton')); row.append(cell); }
            return row;
        }));
        spoken.textContent = own ? historyMessages.loading.own : historyMessages.loading.other;
    };
    const rowFor = entry => {
        const item = historyRow(entry);
        const row = common.element('tr');
        const episode = common.element('td', 'doki-update-episode');
        const who = userName ? userName(entry.userId) : '';
        episode.append(common.element('span', 'doki-update-meta', [item.when, who, item.trigger].filter(Boolean).join(' · ')));
        const name = common.element('span', 'doki-update-name');
        const title = common.element('strong', 'doki-update-title', item.title); title.title = item.title;
        name.append(title);
        if (item.episode) name.append(common.element('span', 'doki-muted doki-update-number', item.episode));
        episode.append(name);
        const result = common.element('td', 'doki-update-result');
        for (const line of item.lines) { const node = common.element('span', 'doki-update-line', line.text); node.dataset.tone = line.tone; result.append(node); }
        row.append(episode);
        if (userName) row.append(common.element('td', 'doki-update-user', who));
        row.append(common.element('td', 'doki-update-trigger', item.trigger), common.element('td', 'doki-update-when', item.when), result);
        return row;
    };
    const failure = async error => {
        if (error?.status === 503) {
            const detail = await error.json().catch(() => null);
            if (detail?.storageUnavailable) return 'storage';
        }
        return 'failed';
    };
    async function show(start = 0) {
        const moving = start !== offset;
        skeleton(moving ? Math.max(body.children.length, 3) : 3);
        previous.disabled = next.disabled = true;
        try {
            const page = await load(start, pageSize);
            if (signal.aborted) return;
            offset = start;
            if (!page.entries.length) message(own ? historyMessages.empty : historyMessages.emptyOther, 'muted');
            else body.replaceChildren(...page.entries.map(rowFor));
            pager.hidden = offset === 0 && !page.more;
            previous.disabled = offset === 0; next.disabled = !page.more;
            range.textContent = pageRange(offset, page.entries.length);
            spoken.textContent = moving ? range.textContent : '';
            if (moving) shell.scrollIntoView?.({ block: 'nearest' });
        } catch (error) {
            if (signal.aborted) return;
            spoken.textContent = '';
            pager.hidden = true;
            if (await failure(error) === 'storage') message(own ? historyMessages.storage : historyMessages.adminStorage, 'attention');
            else message(own ? historyMessages.failed.own : historyMessages.failed.other, 'error', () => show(start));
        } finally {
            if (!signal.aborted) table.removeAttribute('aria-busy');
        }
    }
    previous.addEventListener('click', () => show(Math.max(0, offset - pageSize)), { signal });
    next.addEventListener('click', () => show(offset + pageSize), { signal });
    show(0);
    return { reload: () => show(0) };
}
