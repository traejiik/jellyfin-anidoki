// Plugin Pages inserts its menu entry once and then ignores the DOM, so the entry is lost whenever Jellyfin
// rebuilds the account menu (after playback, for example). Replay its own setup when the entry goes missing.
const runtimeKey = '__anidokiPluginPagesGuard';
// Plugin Pages declares a top-level const, which is a global binding but not a window property.
const findPluginPages = () => typeof PluginPages === 'undefined' ? undefined : PluginPages;

// Same anchor preference as Plugin Pages' onReady: the account menu link, else the legacy drawer.
const menuAnchor = doc => doc.querySelector("[href='#/mypreferencesmenu']") ??
    doc.querySelector('.mainDrawer-scrollContainer > .userMenuOptions');

export function restorePluginPagesEntry(doc, pluginPages) {
    const anchor = menuAnchor(doc);
    if (!pluginPages?.initialized || !anchor || anchor.parentElement.querySelector(':scope > .pluginMenuOptions'))
        return false;
    // The drawer can be rebuilt before the account menu remounts; drop an entry left in the other menu,
    // because onReady does nothing while any marker exists.
    for (const stale of doc.querySelectorAll('.pluginMenuOptions, [data-plugin-pages="true"]'))
        stale.remove();
    pluginPages.onReady();
    pluginPages.populateSidebar();
    return true;
}

export function guardPluginPagesEntry(win = window, doc = document, find = findPluginPages) {
    win[runtimeKey]?.disconnect();
    let queued = false;
    const observer = new win.MutationObserver(() => {
        if (queued)
            return;
        queued = true;
        win.setTimeout(() => {
            queued = false;
            restorePluginPagesEntry(doc, find());
        }, 0);
    });
    observer.observe(doc.body, { childList: true, subtree: true });
    win[runtimeKey] = observer;
    return observer;
}
