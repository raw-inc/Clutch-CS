import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/client-branding.js', import.meta.url), 'utf8');

test('favicon changes one existing link and restores the bundled image for absent or external settings', () =>
{
    let href = '/favicon.svg', changes = 0;
    const icon = {
        getAttribute: () => href,

        setAttribute: (name, value) =>
        {
            assert.equal(name, 'href');
            href = value;
            changes++;
        }
    };
    const context = { window: {}, document: {
        querySelector: selector =>
        {
            assert.equal(selector, 'link[rel="icon"]');
            return icon;
        }
    } };
    vm.runInNewContext(source, context);
    const apply = context.window.contentsBranding.setFavicon;
    apply('/client/branding/favicon?v=first');
    apply('/client/branding/favicon?v=first');
    assert.equal(changes, 1);
    apply('/client/branding/favicon?v=second');
    assert.equal(href, '/client/branding/favicon?v=second');
    apply('/client/branding/header?v=fallback');
    assert.equal(href, '/client/branding/header?v=fallback');
    for (const invalid of [null, undefined, '', 'https://external.example/icon.png', '//external.example/icon.png', 'data:image/svg+xml,'])
    {
        apply(invalid);
        assert.equal(href, '/favicon.svg');
    }
});
