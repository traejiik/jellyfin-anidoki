// Wording for history, last update and account attention, from the server's codes (HistoryReader, AccountAttention).
// Pure, so the Node tests can check every row of the Phase 5 design's Wording tables.
import { providerName, skipReason } from './outcome-lines.js';

const minute = 60 * 1000, hour = 60 * minute, day = 24 * hour;
const plural = (count, unit) => `${count} ${unit}${count === 1 ? '' : 's'}`;
const capital = text => text.charAt(0).toUpperCase() + text.slice(1);
const ordinal = number => {
    const tens = number % 100;
    const suffix = tens >= 11 && tens <= 13 ? 'th' : { 1: 'st', 2: 'nd', 3: 'rd' }[number % 10] ?? 'th';
    return `${number}${suffix}`;
};

/** "just now", "12 minutes ago", "1 hour ago", "yesterday", "3 days ago"; lower case, for use inside a sentence. */
export function relativeTime(time, now = Date.now()) {
    const elapsed = Math.max(0, new Date(now).getTime() - new Date(time).getTime());
    if (elapsed < minute) return 'just now';
    if (elapsed < hour) return `${plural(Math.floor(elapsed / minute), 'minute')} ago`;
    if (elapsed < day) return `${plural(Math.floor(elapsed / hour), 'hour')} ago`;
    const days = Math.floor(elapsed / day);
    return days === 1 ? 'yesterday' : `${days} days ago`;
}

/** The same, standing alone: "Yesterday", "Just now". */
export const relativeLabel = (time, now) => capital(relativeTime(time, now));

export const noRecentUpdates = 'No updates in the last 30 days';

/** The Users table's Last update cell: a time, "No updates…" when linked but nothing in retention, "—" when nothing is linked. */
export function lastUpdateCell(lastUpdateUtc, linked, now) {
    if (!linked) return '—';
    return lastUpdateUtc ? relativeLabel(lastUpdateUtc, now) : noRecentUpdates;
}

/** A linked account row's second line. */
export const lastUpdateLine = (lastUpdateUtc, now) => lastUpdateUtc ? `Last update ${relativeTime(lastUpdateUtc, now)}` : noRecentUpdates;

/**
 * An account's attention line, or '' when it needs none. surface: 'table' (short), 'user' (their own page) or 'admin'
 * (the admin's panel for userName).
 */
export function attentionText(provider, attention, { surface, userName = 'the user', now } = {}) {
    if (!attention) return '';
    const name = providerName(provider), when = relativeTime(attention.sinceUtc, now), token = provider === 'Annict';
    if (attention.kind === 'UpdatesFailing') {
        if (surface === 'table') return `${name} updates are failing`;
        return `Updates are failing: ${name} was ${attention.step === 'transient' ? 'unreachable' : 'busy'}. Tried 4 times over 7 hours, then stopped.`;
    }
    if (attention.kind !== 'SignInRejected') return '';
    if (surface === 'table') return `${name} needs a new ${token ? 'token' : 'sign-in'}`;
    if (token) return surface === 'admin'
        ? `Annict rejected ${userName}’s token ${when}. Nothing is sent to Annict until you add a new one.`
        : `Annict rejected your token ${when}. Nothing is sent to Annict until your administrator adds a new one.`;
    return surface === 'admin'
        ? `${name} rejected the sign-in ${when}. Nothing is sent to ${name} until ${userName} signs in again.`
        : `${name} rejected your sign-in ${when}. Nothing is sent to ${name} until you sign in again.`;
}

/** One tracker's line in a history entry: { text, tone }. Tones: success, attention, muted. */
export function resultLine(result) {
    const name = providerName(result.provider);
    switch (result.result) {
        case 'Updated': return { text: result.try ? `${name} updated on the ${ordinal(result.try)} try` : `${name} updated`, tone: 'success' };
        case 'Retrying': return { text: `${name} failed, retrying`, tone: 'attention' };
        case 'GaveUp': return { text: `${name} failed, gave up`, tone: 'attention' };
        case 'NeedsSignIn': return { text: `${name} needs a new ${result.provider === 'Annict' ? 'token' : 'sign-in'}`, tone: 'attention' };
        case 'Unconfirmed': return { text: `${name} may have updated; check it`, tone: 'attention' };
        case 'Skipped': {
            const reason = skipReason(result.step);
            return { text: reason ? `${name} skipped: ${reason.charAt(0).toLowerCase()}${reason.slice(1)}` : `${name} skipped`, tone: 'muted' };
        }
        case 'Unresolved': return { text: `${name}: no matching entry`, tone: 'muted' };
        default: return { text: `${name} failed`, tone: 'attention' };
    }
}

export const noChangeLine = { text: 'No change needed', tone: 'muted' };

const triggers = { Playback: 'Playback', WatchedToggle: 'Marked watched', ConfirmedManual: 'Confirmed mark', Retry: 'Retry' };

/** A history entry ready to render: title, episode label, trigger, when, and its result lines. */
export function historyRow(entry, now) {
    return {
        id: entry.operationId,
        title: entry.title,
        episode: Number.isInteger(entry.episode) ? `Episode ${entry.episode}` : '',
        trigger: triggers[entry.trigger] ?? 'Update',
        when: relativeLabel(entry.whenUtc, now),
        lines: entry.results?.length ? entry.results.map(resultLine) : [noChangeLine]
    };
}

export const historyMessages = {
    loading: { own: 'Loading your history…', other: 'Loading history…' },
    failed: { own: 'Could not load your history. Check your connection to Jellyfin, then try again.', other: 'Could not load this history. Check your connection to Jellyfin, then try again.' },
    storage: 'History is unavailable because AniDōki could not open its storage. Trackers are not being updated either. Your administrator can see why on the AniDōki settings page.',
    adminStorage: 'History is unavailable because AniDōki could not open its storage. Trackers are not being updated either. The AniDōki settings page says why.',
    empty: 'No updates in the last 30 days. They appear here after you watch an episode or mark one watched.',
    emptyOther: 'No updates in the last 30 days. They appear here after a user watches an episode or marks one watched.'
};

/** The pager's words for the rows shown: "11–20 of 42". */
export const pageRange = (offset, count, total) => count ? `${offset + 1}–${offset + count}${Number.isInteger(total) ? ` of ${total}` : ''}` : '';
