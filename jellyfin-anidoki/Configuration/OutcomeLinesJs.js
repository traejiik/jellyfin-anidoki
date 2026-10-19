// One outcome → line mapping for the playback toast and the manual card, so their wording cannot drift.
// Discord says the same in Notifications/Discord/DiscordOutcomeText.cs.
export const providerNames = { AniList: 'AniList', Mal: 'MyAnimeList', Kitsu: 'Kitsu', Annict: 'Annict', Shikimori: 'Shikimori', Simkl: 'Simkl' };
export const providerName = id => providerNames[id] ?? 'Tracker';
export const list = values => values.length < 2 ? values[0] : `${values.slice(0, -1).join(', ')} and ${values.at(-1)}`;

// Step codes recorded by UpdateProviderStatus.RecordDecision; the line icon already says "skipped".
const reasons = {
    'already-watched': 'Episode already watched',
    'manual-forward-only': 'Already at or past this episode',
    'rewatch-disabled': 'Completed, and rewatching is off',
    'rewatch-unsupported': 'Rewatching is not supported',
    'plan-to-watch-only': 'Not on Plan to Watch'
};
const results = { NoChange: 'Already up to date', Skipped: 'Skipped', Unresolved: 'No matching entry found', Failed: 'Update failed', Unconfirmed: 'Update not confirmed' };
// Second line under a result that names no entry, where the toast has no reason sentence of its own.
const notes = { Failed: 'Not retried automatically.', Unconfirmed: "It may have taken effect. It won't be retried automatically." };
// Read out in place of the icon, which is decoration.
const spoken = { Confirmed: 'Updated', NoChange: 'No change', Skipped: 'Skipped', Unresolved: 'Unresolved', Failed: 'Failed', Unconfirmed: 'Not confirmed' };
const tones = { Confirmed: 'success', NoChange: 'neutral', Skipped: 'attention', Unresolved: 'attention', Unconfirmed: 'attention', Failed: 'error' };
const icons = { Confirmed: 'check', NoChange: 'dash', Skipped: 'skip', Unresolved: 'question', Unconfirmed: 'triangle', Failed: 'cross' };
const text = value => String(value ?? '').slice(0, 256);
const comparable = value => String(value ?? '').normalize('NFKD').replace(/[^\p{L}\p{N}]+/gu, '').toLowerCase();

/** The tracker entry is named only when it differs from the Jellyfin series, e.g. a split season's STAGE entry. */
export function showsEntryTitle(outcome, sourceTitle) {
    return Boolean(outcome.targetTitle) && comparable(outcome.targetTitle) !== comparable(sourceTitle);
}

/**
 * A line reads `entry` over `text` when the entry is named, else `text` over `note`.
 * Confirmed: "AniList · Episode 1 · Completed"; others: the reason, then the episode when the outcome has one.
 */
export function describeOutcome(outcome, sourceTitle, { isMovie = false, withNotes = false } = {}) {
    const status = outcome.status?.replaceAll('_', ' ');
    const episode = !isMovie && !outcome.statusOnly && Number.isInteger(outcome.progress) ? `Episode ${outcome.progress}` : '';
    const result = outcome.kind === 'Confirmed'
        ? [episode, status].filter(Boolean).join(' · ') || 'Updated'
        : [reasons[outcome.step] ?? results[outcome.kind] ?? 'Updated', episode].filter(Boolean).join(' · ');
    const entry = showsEntryTitle(outcome, sourceTitle) ? text(outcome.targetTitle) : '';
    return {
        tone: tones[outcome.kind] ?? 'neutral',
        icon: icons[outcome.kind] ?? 'dash',
        spoken: spoken[outcome.kind] ?? 'Result',
        entry,
        text: `${providerName(outcome.provider)} · ${result}`,
        note: withNotes && !entry ? notes[outcome.kind] ?? '' : ''
    };
}

/** One line per tracker entry and result, in the order the sync produced them; a repeat keeps its last position. */
export function outcomeLines(outcomes, sourceTitle, options) {
    const latest = new Map();
    for (const outcome of outcomes ?? []) {
        const key = `${outcome.provider}:${outcome.targetId}:${outcome.kind}:${outcome.step}`;
        latest.delete(key);
        latest.set(key, outcome);
    }
    return [...latest.values()].map(outcome => describeOutcome(outcome, sourceTitle, options));
}
