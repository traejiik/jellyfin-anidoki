export function createDiscordDraft(saved) { return { enabled: !!saved.enabled, webhook: '', clear: false }; }
export function discordCommand(draft, saved) { return { enabled: !draft.clear && draft.enabled, webhookAction: draft.clear ? 'Clear' : draft.webhook.trim() ? 'Replace' : 'Keep', webhook: draft.clear ? null : draft.webhook.trim() || null, expectedRevision: saved.revision }; }
export function discordDirty(draft, saved) { return draft.enabled !== saved.enabled || !!draft.webhook.trim() || draft.clear; }
// Discord settings save with the rest of the page: the page's Save changes calls cache.discordSave
// and Discard calls cache.discordDiscard. onChange lets the page show or hide its save bar.
export default async function mountDiscord(root, common, cache, signal, isHostBusy, onChange = () => {}) {
    const find = id => root.querySelector(`#${id}`);
    let saved = await common.json('AniDoki/user/discord/settings', { signal });
    if (signal.aborted) return;
    const webhook = find('discordWebhook');
    find('discordWebhookControl').replaceChildren(common.secretInput(webhook, 'Discord webhook', signal));
    const draft = cache.discordDraft ??= createDiscordDraft(saved);
    cache.discordDirty = () => discordDirty(draft, saved);
    cache.discordSave = async saveSignal => {
        if (!discordDirty(draft, saved)) return;
        saved = await common.json('AniDoki/user/discord/settings', { method: 'PUT', body: discordCommand(draft, saved), signal: saveSignal });
        Object.assign(draft, createDiscordDraft(saved)); status(''); render();
    };
    cache.discordDiscard = () => { Object.assign(draft, createDiscordDraft(saved)); status(''); render(); };
    cache.discordRefresh = () => render();
    let busy = false, objectUrl, previewGeneration = 0;
    const status = (text, kind) => common.status(find('discordStatus'), text, kind);
    const changed = () => { render(); onChange(); };
    function render() {
        find('discordEnabled').checked = draft.enabled;
        find('discordDetail').hidden = !draft.enabled;
        if (webhook.value !== draft.webhook) webhook.value = draft.webhook;
        find('discordWebhook').placeholder = saved.hasWebhook ? 'Webhook saved · leave blank to keep it' : 'https://discord.com/api/webhooks/…';
        find('discordHelp').textContent = saved.publicAddressReady ? 'AniDoki uses its own name and avatar. Your webhook is kept in the AniDōki database on the server.' : 'Ask your administrator to set the server Public address. AniDoki uses its own name and avatar.';
        for (const id of ['discordEnabled','discordWebhook','clearDiscord','testDiscord']) find(id).disabled = busy;
        find('testDiscord').disabled = busy || !saved.hasWebhook || discordDirty(draft, saved);
        find('clearDiscord').disabled = busy || !saved.hasWebhook && !draft.webhook;
    }
    async function action(callback) {
        if (busy || isHostBusy()) return; busy = true; render();
        try { await callback(); } catch (error) { if (!signal.aborted) status(await common.errorMessage(error), 'error'); }
        finally { busy = false; if (!signal.aborted) render(); }
    }
    // Turning Discord off drops an unsaved webhook edit, so the hidden field never saves by surprise.
    find('discordEnabled').addEventListener('change', e => { draft.enabled = e.target.checked; draft.clear = false; if (!draft.enabled) draft.webhook = ''; changed(); }, { signal });
    find('discordWebhook').addEventListener('input', e => { draft.webhook = e.target.value; draft.clear = false; changed(); }, { signal });
    find('clearDiscord').addEventListener('click', () => { draft.clear = true; draft.webhook = ''; status('Webhook removal is pending. Save changes to remove it and turn off Discord updates.'); changed(); }, { signal });
    find('testDiscord').addEventListener('click', () => action(async () => {
        if (discordDirty(draft, saved)) throw new Error('Save changes before sending a test.');
        status('Waiting for Discord to confirm the test…');
        const result = await common.json('AniDoki/user/discord/test', { method: 'POST', body: { expectedRevision: saved.revision }, signal });
        if (!signal.aborted) status(result.message, result.confirmed ? 'success' : 'error');
    }), { signal });
    const clearPoster = () => { if (objectUrl) URL.revokeObjectURL(objectUrl); objectUrl = null; };
    signal.addEventListener('abort', clearPoster, { once: true });
    find('discordPreviewToggle').addEventListener('toggle', async e => {
        const generation = ++previewGeneration;
        const preview = find('discordPreview'); preview.hidden = !e.target.open; clearPoster(); preview.replaceChildren();
        if (preview.hidden) return;
        preview.append(common.element('p','doki-help','Loading preview…'));
        try {
            const sample = await common.json('AniDoki/user/discord/preview', { signal });
            if (signal.aborted || generation !== previewGeneration || !find('discordPreviewToggle').open) return; preview.replaceChildren();
            if (sample.empty) { preview.append(common.element('p','doki-help',sample.message)); return; }
            const header = common.element('div','doki-discord-preview-header');
            const avatar = common.element('img','doki-discord-avatar');
            avatar.src = common.assetUrl('anidoki-avatar.png'); avatar.alt = '';
            const badge = common.element('span','doki-discord-app','APP');
            const previewTime = new Date(sample.timestamp ?? Date.now()).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            const time = common.element('span','doki-discord-time',`Today at ${previewTime}`);
            header.append(avatar,common.element('strong','doki-discord-name','AniDoki'),badge,time);
            const body = common.element('div','doki-discord-preview-body'), text = common.element('div','doki-discord-preview-text'), title = common.element('strong','');
            if (sample.showUrl && /^https?:\/\//.test(sample.showUrl)) { const link = common.element('a','',sample.showTitle); link.href = sample.showUrl; link.target = '_blank'; link.rel = 'noopener noreferrer'; title.append(link); }
            else title.textContent = sample.showTitle;
            text.append(title, common.element('p','doki-discord-episode',sample.episodeLine));
            for (const field of sample.fields ?? []) {
                const result = common.element('div','doki-discord-result');
                result.append(common.element('strong','',field.name), common.element('p','',field.value)); text.append(result);
            }
            const footer = common.element('div','doki-discord-footer');
            const footerAvatar = common.element('img','doki-discord-footer-avatar');
            footerAvatar.src = sample.footerIconUrl ?? ''; footerAvatar.alt = 'Jellyfin';
            footer.append(footerAvatar,common.element('span','',`${sample.serverName || 'Jellyfin'} · Today at ${previewTime}`));
            text.append(footer);
            body.append(text); preview.append(header,body,common.element('p','doki-discord-example-note','Example tracker results · Preview only · No message sent'));
            const posterStatus = common.element('small','doki-discord-poster-fallback','Poster unavailable.');body.append(posterStatus);
            if (sample.posterItemId) {
                const response = await common.request(`AniDoki/user/discord/preview/poster?itemId=${encodeURIComponent(sample.posterItemId)}`, { signal });
                const blob = await response.blob(); if (signal.aborted || generation !== previewGeneration || !find('discordPreviewToggle').open) return;
                objectUrl = URL.createObjectURL(blob); const poster = common.element('img','doki-discord-poster'); poster.src = objectUrl; poster.alt = `${sample.showTitle} poster`; posterStatus.remove(); body.append(poster);
            }
        } catch (error) { if (!signal.aborted && generation === previewGeneration && find('discordPreviewToggle').open) preview.append(common.element('p','doki-help','Poster or preview unavailable.')); }
    }, { signal });
    render(); if (saved.deliveryStatus) status(saved.deliveryStatus);
}
