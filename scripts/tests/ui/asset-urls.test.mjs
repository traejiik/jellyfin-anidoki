import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';

test('asset URLs share one versioned path segment per page load', async t => {
    t.after(() => { delete globalThis.ApiClient; delete globalThis.anidokiAssetVersion; });
    globalThis.ApiClient = { getUrl: path => path };
    delete globalThis.anidokiAssetVersion;
    const { assetUrl } = await importConfiguration('CommonJs.js');
    const styles = assetUrl('styles.css'), common = assetUrl('common.js');
    assert.match(styles, /^AniDoki\/assets\/v\/[0-9a-z]+\/styles\.css$/);
    assert.equal(common.replace('common.js', 'styles.css'), styles);
    globalThis.anidokiAssetVersion = 'pinned';
    assert.equal(assetUrl('common.js'), 'AniDoki/assets/v/pinned/common.js');
});
