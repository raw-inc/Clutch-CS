import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser.js', import.meta.url), 'utf8');

for (const [name, content, type] of [
    ['workflow.yaml', 'id: example\nname: 日本語\nnodes: []\n', 'application/yaml'],
    ['workflow.json', '{"id":"example","name":"日本語","nodes":[]}', undefined]
])
{
    test(name + ' preserves the downloaded content and media type', async () =>
    {
        let blob, revoked, cleanup;
        const link = { click: () =>
        {
            link.clicked = true;
        } };
        const context = {
            window: {},
            Blob,
            document: { addEventListener: () => undefined,

                createElement: tag =>
                {
                    assert.equal(tag, 'a');
                    return link;
                } },
            URL: {
                createObjectURL: value =>
                {
                    blob = value;
                    return 'blob:export';
                },

                revokeObjectURL: value =>
                {
                    revoked = value;
                }
            },

            setTimeout: callback =>
            {
                cleanup = callback;
            }
        };
        vm.runInNewContext(source, context);
        context.window.contentsBrowser.download(name, content, type);
        assert.equal(link.clicked, true);
        assert.equal(link.download, name);
        assert.equal(link.href, 'blob:export');
        assert.equal(blob.type, type || 'application/json');
        assert.equal(await blob.text(), content);
        cleanup();
        assert.equal(revoked, 'blob:export');
    });
}
