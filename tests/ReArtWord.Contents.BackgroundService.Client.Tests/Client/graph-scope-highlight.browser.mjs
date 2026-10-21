import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { chromium, firefox } from 'playwright';

let server, origin;
before(async () =>
{
    const script = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-scope-highlight.js', import.meta.url), 'utf8');
    server = createServer((request, response) =>
    {
        if (request.url === '/scope.js')
        {
            response.setHeader('Content-Type', 'text/javascript');
            response.end(script);
            return;
        }
        response.setHeader('Content-Type', 'text/html');
        response.end('<!doctype html><input id="field"><svg id="graph" tabindex="0"><g id="top" class="graph-node" data-selected-scope="0"></g><g id="outer" class="graph-node" data-selected-scope="1" data-scope-membership="1"></g><g id="inner" class="graph-node selected" data-selected-scope="2" data-scope-membership="1 2"></g><g id="body" class="graph-node" data-selected-scope="2" data-scope-membership="1 2"></g><g id="other" class="graph-node" data-selected-scope="3" data-scope-membership="3"></g></svg><script type="module">import {attachGraphScopeHighlight} from "/scope.js"; window.scope=attachGraphScopeHighlight(document.querySelector("svg"));</script>');
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () =>
{
    await new Promise(resolve =>
    {
        server.close(resolve);
    });
});

for (const [name, engine] of Object.entries({ chromium, firefox }))
{
    test(`${name}: Shift follows the selected execution scopes and releases all listeners`, async () =>
    {
        const browser = await engine.launch({ headless: true });
        try
        {
            const page = await browser.newPage();
            await page.goto(origin);
            await page.waitForFunction(() => window.scope !== undefined);
            await page.locator('#graph').focus();
            await page.keyboard.down('Shift');
            assert.deepEqual(await page.locator('.outside-scope').evaluateAll(nodes => nodes.map(node => node.id)), ['top', 'outer', 'other']);
            await page.evaluate(() =>
            {
                document.querySelector('#other').classList.add('selected');
                window.scope.update();
            });
            assert.deepEqual(await page.locator('.outside-scope').evaluateAll(nodes => nodes.map(node => node.id)), ['top', 'outer']);
            await page.evaluate(() =>
            {
                document.querySelector('#top').classList.add('selected');
                window.scope.update();
            });
            assert.equal(await page.locator('.outside-scope').count(), 0);
            await page.evaluate(() =>
            {
                document.querySelectorAll('.selected').forEach(node => node.classList.remove('selected'));
                document.querySelector('#outer').classList.add('selected');
                window.scope.update();
            });
            assert.deepEqual(await page.locator('.outside-scope').evaluateAll(nodes => nodes.map(node => node.id)), ['top', 'other']);
            await page.keyboard.up('Shift');
            assert.equal(await page.locator('.outside-scope').count(), 0);
            await page.locator('#field').focus();
            await page.keyboard.down('Shift');
            assert.equal(await page.locator('.outside-scope').count(), 0);
            await page.keyboard.up('Shift');
            await page.locator('#graph').focus();
            await page.keyboard.down('Shift');
            await page.evaluate(() => window.dispatchEvent(new Event('blur')));
            assert.equal(await page.locator('.outside-scope').count(), 0);
            await page.keyboard.up('Shift');
            await page.keyboard.down('Shift');
            await page.evaluate(() => window.scope.dispose());
            assert.equal(await page.locator('.outside-scope').count(), 0);
            await page.keyboard.up('Shift');
            await page.keyboard.down('Shift');
            assert.equal(await page.locator('.scope-highlight').count(), 0);
        }
        finally
        {
            await browser.close();
        }
    });
}
