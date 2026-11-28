import { statusOrder, statusLabel } from './popup-state.js';

export const libraryStatuses = ['all', ...statusOrder, 'NotOnList'];
export function libraryStatusLabel(status) {
    return status === 'all' ? 'All statuses' : status === 'NotOnList' ? 'Not on your list' : statusLabel(status, 'AniList');
}

export function libraryResults(view, { type, status, query }) {
    const rows = (view?.rows ?? []).filter(row => type === 'all' || row.isMovie === (type === 'movie'));
    const counts = Object.fromEntries(libraryStatuses.map(key => [key, 0]));
    counts.all = rows.length;
    const search = query.trim().toLocaleLowerCase();
    const shown = rows.filter(row => {
        const key = row.matchState === 'NotOnList' ? 'NotOnList' : row.matchState === 'OnList' ? row.status : null;
        if (key && Object.hasOwn(counts, key))
            counts[key]++;
        return (status === 'all' || status === key) && (!search || row.title.toLocaleLowerCase().includes(search));
    });
    shown.sort((left, right) => Number(left.matchState === 'Unresolved') - Number(right.matchState === 'Unresolved'));
    return { rows: shown, counts };
}

export function libraryCard(row, nowMs = Date.now()) {
    const onList = row.matchState === 'OnList';
    const total = row.isMovie ? 1 : row.totalEpisodes;
    const progress = onList ? row.progress : null;
    const unlogged = onList ? row.unloggedLibraryCount : null;
    const bounded = Math.min(total, Math.max(0, progress));
    const solid = progress != null && total > 0 ? bounded / total : null;
    const tint = solid === null ? null : Math.min(total - bounded, Math.max(0, unlogged ?? 0)) / total;
    const hours = Math.ceil((Date.parse(row.nextAiringAtUtc) - nowMs) / 3_600_000);
    const days = Math.ceil(hours / 24);
    let countdown = null;
    if (hours > 0)
        countdown = hours === 1 ? 'Within an hour' : hours <= 24 ? hours + ' hours' : days + ' days';
    return {
        status: row.matchState === 'Unresolved' ? 'Match unavailable' : libraryStatusLabel(onList ? row.status : 'NotOnList'),
        progress: progress == null ? null : total > 0 ? progress + '/' + total : String(progress),
        unlogged, solid, tint, countdown,
        score: onList && row.score > 0 ? String(row.score / 10) : null
    };
}
