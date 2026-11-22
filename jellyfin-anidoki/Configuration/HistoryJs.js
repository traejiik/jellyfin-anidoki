// The History table on the user's page and in the admin's per-user panel, with its loading, failed, storage and empty
// states and Show 10 more. Every state keeps the heading row, so the panel does not jump when rows arrive.
import { historyMessages, historyRow } from './history-state.js';

const columns = ['Episode', 'Trigger', 'When', 'Result'];
export const pageSize = 10;

/**
 * Renders into container. load(offset, limit) resolves to { entries, more } or rejects with the failed Response.
 * own: the signed-in user's history (wording "your"); first: how many rows to show before Show 10 more.
 */
export default function renderHistory(container, { load, own = true, first = pageSize }, common, signal) {
    const table = common.element('table', 'doki-update-table');
    const head = common.element('thead'), headRow = common.element('tr');
    for (const name of columns) { const cell = common.element('th', `doki-update-${name.toLowerCase()}`, name); cell.scope = 'col'; headRow.append(cell); }
    head.append(headRow);
    const body = common.element('tbody');
    table.append(head, body);
    const shell = common.element('div', 'doki-table-shell'); shell.append(table);
    const spoken = common.element('p', 'doki-sr-only'); spoken.setAttribute('role', 'status');
    const more = common.element('button', 'doki-button doki-update-more', `Show ${pageSize} more`); more.type = 'button'; more.hidden = true;
    const feedback = common.element('p', 'doki-status'); feedback.setAttribute('role', 'status');
    const actions = common.element('div', 'doki-actions'); actions.append(more);
    container.classList.add('doki-history');
    container.replaceChildren(shell, actions, feedback, spoken);
    let shown = 0;

    const message = (text, tone, retry) => {
        const row = common.element('tr'), cell = common.element('td', 'doki-update-message'); cell.colSpan = columns.length;
        cell.dataset.tone = tone;
        const words = common.element('p');
        if (tone === 'attention') words.append(common.icon('triangle'));
        words.append(text); cell.append(words);
        if (retry) { const again = common.element('button', 'doki-button', 'Try again'); again.type = 'button'; again.addEventListener('click', retry, { signal }); cell.append(again); }
        row.append(cell); body.replaceChildren(row);
    };
    const skeleton = () => {
        table.setAttribute('aria-busy', 'true');
        body.replaceChildren(...Array.from({ length: 3 }, () => {
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
        episode.append(common.element('span', 'doki-update-meta', `${item.when} · ${item.trigger}`));
        const name = common.element('span', 'doki-update-name');
        const title = common.element('strong', 'doki-update-title', item.title); title.title = item.title;
        name.append(title);
        if (item.episode) name.append(common.element('span', 'doki-muted doki-update-number', item.episode));
        episode.append(name);
        const result = common.element('td', 'doki-update-result');
        for (const line of item.lines) { const node = common.element('span', 'doki-update-line', line.text); node.dataset.tone = line.tone; result.append(node); }
        row.append(episode, common.element('td', 'doki-update-trigger', item.trigger), common.element('td', 'doki-update-when', item.when), result);
        return row;
    };
    const failure = async error => {
        if (error?.status === 503) {
            const detail = await error.json().catch(() => null);
            if (detail?.storageUnavailable) return 'storage';
        }
        return 'failed';
    };
    async function fill() {
        skeleton(); more.hidden = true; common.status(feedback, '');
        try {
            const page = await load(0, first);
            if (signal.aborted) return;
            shown = page.entries.length;
            if (!shown) message(historyMessages.empty, 'muted');
            else body.replaceChildren(...page.entries.map(rowFor));
            more.hidden = !page.more;
            spoken.textContent = '';
        } catch (error) {
            if (signal.aborted) return;
            spoken.textContent = '';
            if (await failure(error) === 'storage') message(own ? historyMessages.storage : historyMessages.adminStorage, 'attention');
            else message(own ? historyMessages.failed.own : historyMessages.failed.other, 'error', fill);
        } finally {
            if (!signal.aborted) table.removeAttribute('aria-busy');
        }
    }
    more.addEventListener('click', async () => {
        more.disabled = true; common.status(feedback, 'Loading…');
        try {
            const page = await load(shown, pageSize);
            if (signal.aborted) return;
            body.append(...page.entries.map(rowFor)); shown += page.entries.length;
            more.hidden = !page.more; common.status(feedback, '');
        } catch (error) {
            if (!signal.aborted) common.status(feedback, await failure(error) === 'storage'
                ? (own ? historyMessages.storage : historyMessages.adminStorage)
                : 'Could not load more. Check your connection to Jellyfin, then try again.', 'error');
        } finally {
            if (!signal.aborted) more.disabled = false;
        }
    }, { signal });
    fill();
    return { reload: fill };
}
