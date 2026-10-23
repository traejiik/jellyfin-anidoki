import { readFile } from 'node:fs/promises';

// The browser resolves './x.js' imports through AniDokiController's served asset names; Node cannot, so each
// import of a served name is inlined as a data URL of the file that serves it.
const served = { 'outcome-lines.js': 'OutcomeLinesJs.js', 'notification-state.js': 'NotificationStateJs.js', 'manual-mark-state.js': 'ManualMarkStateJs.js', 'plugin-pages-guard.js': 'PluginPagesGuardJs.js' };

export async function configurationUrl(file) {
    let source = await readFile(new URL(`../../../jellyfin-anidoki/Configuration/${file}`, import.meta.url), 'utf8');
    for (const [asset, name] of Object.entries(served))
        if (source.includes(`'./${asset}'`))
            source = source.replaceAll(`'./${asset}'`, JSON.stringify(await configurationUrl(name)));
    return 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');
}

export const importConfiguration = async file => import(await configurationUrl(file));
