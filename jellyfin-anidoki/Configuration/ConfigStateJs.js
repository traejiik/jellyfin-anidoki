// Only editable fields enter a draft. Credentials refreshed by account actions
// and fields from newer plugin versions stay in the freshly loaded configuration.
const serverFields = ['callbackUrl', 'callbackRedirectUrl', 'animeListSaveLocation',
    'watchedTickboxUpdatesProvider', 'shikimoriAppName', 'simklUpdateAll', 'updateNsfw',
    'authenticationLinkExpireTimeMinutes', 'armServerBaseUrl', 'enableUserPages', 'shikimoriDomain'];
const providerFields = ['ClientId', 'ClientSecret'];
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
    result.ProviderApiAuth = (saved.ProviderApiAuth ?? []).map(provider => ({
        Name: provider.Name, ClientId: provider.ClientId ?? '', ClientSecret: provider.ClientSecret ?? ''
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
        let target = value;
        if (change.group !== 'server') {
            const collection = change.group === 'provider' ? 'ProviderApiAuth' : 'UserConfig';
            const key = change.group === 'provider' ? 'Name' : 'UserId';
            const list = value[collection] ?? [];
            target = list.find(item => item[key] === change.id);
            const defaults = change.group === 'user' ? userPreferences() : { ClientId: '', ClientSecret: '' };
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

export function validateLibraries(mode, ids) {
    const selected = [...new Set(ids)];
    return mode === 'all' ? { valid: true, ids: [] } : { valid: selected.length > 0, ids: selected };
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
