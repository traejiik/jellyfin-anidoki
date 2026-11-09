// Only editable fields enter a draft. Credentials refreshed by account actions
// and fields from newer plugin versions stay in the freshly loaded configuration.
const serverFields = ['callbackUrl', 'callbackRedirectUrl', 'animeListSaveLocation',
    'watchedTickboxUpdatesProvider', 'shikimoriAppName', 'simklUpdateAll', 'updateNsfw', 'applySavedMatches',
    'authenticationLinkExpireTimeMinutes', 'armServerBaseUrl', 'enableUserPages', 'shikimoriDomain', 'AnimeLibraries'];
const providerFields = ['ClientId'];
// Client secrets are write-only: the server never returns them, so a draft holds only a replacement or a clear request.
const secretDefaults = { ClientSecret: '', ClearClientSecret: false };
const preferenceFields = ['PlanToWatchOnly', 'RewatchCompleted', 'ShowLogNotifications', 'LibraryToCheck'];
const clone = value => value === undefined ? undefined : structuredClone(value);
const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);

export function userPreferences(saved = {}) {
    return {
        PlanToWatchOnly: saved.PlanToWatchOnly ?? true,
        RewatchCompleted: saved.RewatchCompleted ?? true,
        ShowLogNotifications: saved.ShowLogNotifications ?? true,
        LibraryToCheck: [...(saved.LibraryToCheck ?? [])]
    };
}

export function createDraft(saved = {}) {
    const result = {};
    for (const field of serverFields) result[field] = clone(saved[field]);
    // An older configuration without the list would otherwise conflict with the server's empty one at save time.
    result.AnimeLibraries ??= [];
    result.ProviderApiAuth = (saved.ProviderApiAuth ?? []).map(provider => ({
        Name: provider.Name,
        ClientId: provider.ClientId ?? '',
        ClientSecret: provider.ClientSecret ?? secretDefaults.ClientSecret,
        ClearClientSecret: provider.ClearClientSecret ?? secretDefaults.ClearClientSecret
    }));
    result.UserConfig = (saved.UserConfig ?? []).map(user => ({ UserId: user.UserId, ...userPreferences(user) }));
    return result;
}

export function changedValues(baseline, draft) {
    const changes = [];
    const add = (group, id, field, before, after) => {
        if (!equal(before, after)) changes.push({ group, id, field, before: clone(before), value: clone(after) });
    };
    for (const field of serverFields) add('server', null, field, baseline[field], draft[field]);
    for (const provider of draft.ProviderApiAuth ?? []) {
        const old = baseline.ProviderApiAuth?.find(item => item.Name === provider.Name);
        for (const field of providerFields) add('provider', provider.Name, field, old?.[field] ?? '', provider[field] ?? '');
        for (const [field, empty] of Object.entries(secretDefaults)) add('secret', provider.Name, field, old?.[field] ?? empty, provider[field] ?? empty);
    }
    for (const user of draft.UserConfig ?? []) {
        const old = userPreferences(baseline.UserConfig?.find(item => item.UserId === user.UserId));
        for (const field of preferenceFields) add('user', user.UserId, field, old[field], user[field]);
    }
    return changes;
}

export function mergeChanges(latest, baseline, draft) {
    const value = clone(latest);
    const conflicts = [];
    for (const change of changedValues(createDraft(baseline), draft)) {
        if (change.group === 'secret') continue;
        let target = value;
        if (change.group !== 'server') {
            const collection = change.group === 'provider' ? 'ProviderApiAuth' : 'UserConfig';
            const key = change.group === 'provider' ? 'Name' : 'UserId';
            const list = value[collection] ?? [];
            target = list.find(item => item[key] === change.id);
            const defaults = change.group === 'user' ? userPreferences() : { ClientId: '' };
            const remote = target?.[change.field] ?? defaults[change.field];
            if (!equal(remote, change.before) && !equal(remote, change.value)) {
                conflicts.push(`${change.id}.${change.field}`); continue;
            }
            if (!target) {
                target = { [key]: change.id, ...defaults };
                value[collection] = list; list.push(target);
            }
        } else if (!equal(target[change.field], change.before) && !equal(target[change.field], change.value)) {
            conflicts.push(change.field); continue;
        }
        target[change.field] = clone(change.value);
    }
    return { value, conflicts };
}

export function mergePreferences(latest, baseline, draft) {
    const value = userPreferences(latest);
    const conflicts = [];
    for (const field of preferenceFields) {
        if (equal(baseline[field], draft[field])) continue;
        if (!equal(value[field], baseline[field]) && !equal(value[field], draft[field])) conflicts.push(field);
        else value[field] = clone(draft[field]);
    }
    return { value, conflicts };
}

export function secretChanges(draft) {
    return (draft.ProviderApiAuth ?? []).flatMap(provider => {
        if (provider.ClearClientSecret) return [{ Provider: provider.Name, Clear: true }];
        if (provider.ClientSecret) return [{ Provider: provider.Name, Secret: provider.ClientSecret, Clear: false }];
        return [];
    });
}

export function withoutSecrets(configuration) {
    const value = clone(configuration);
    for (const provider of value.ProviderApiAuth ?? []) {
        delete provider.ClientSecret;
        delete provider.ClearClientSecret;
    }
    return value;
}

// Library IDs reach the page as GUIDs with or without dashes, so they compare in one form.
const libraryKey = id => String(id ?? '').replace(/-/g, '').toLowerCase();
export const animeLibraryModes = ['Dedicated', 'Mixed', 'Off'];
// '' while the admin has not chosen any library: each user's own choice still applies then.
export function animeLibraryMode(list, libraryId) {
    const entry = (list ?? []).find(item => libraryKey(item.LibraryId) === libraryKey(libraryId));
    return entry?.Mode ?? (list?.length ? 'Off' : '');
}
export function withAnimeLibraryMode(list, libraryId, mode) {
    const current = list ?? [];
    const matches = item => libraryKey(item.LibraryId) === libraryKey(libraryId);
    return current.some(matches) ? current.map(item => matches(item) ? { ...item, Mode: mode } : item) : [...current, { LibraryId: libraryId, Mode: mode }];
}
export function describeAnimeLibraries(list, libraries) {
    if (!list?.length) return 'Not chosen yet';
    const scanned = libraries.filter(library => animeLibraryMode(list, library.Id) !== 'Off').length;
    return scanned ? `${scanned} of ${libraries.length} ${libraries.length === 1 ? 'library' : 'libraries'} scanned` : 'Nothing will sync';
}
// Only shows, movies and mixed-content libraries can hold what AniDōki syncs; the rest are never listed.
const libraryKinds = { tvshows: 'Shows', movies: 'Movies', mixed: 'Mixed content' };
export function animeLibraryChoices(libraries) {
    return (libraries ?? [])
        .filter(library => !library.Type || library.Type in libraryKinds)
        .map(library => ({ ...library, kind: libraryKinds[library.Type] ?? 'Mixed content' }));
}
