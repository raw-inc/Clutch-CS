import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium, firefox } from 'playwright';

const root = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url));
const fixture = fileURLToPath(new URL('./fixtures/local-files.mjs', import.meta.url));
let server, origin;
before(async() =>
{
    server = createServer(async(request, response) =>
    {
        try
        {
            const pathname = new URL(request.url, 'http://localhost').pathname;
            if (pathname === '/')
            {
                response.setHeader('Content-Type', 'text/html; charset=utf-8');
                response.end('<!doctype html><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/remote-tools.css"><main id="tool"></main>');
                return;
            }
            const file = pathname === '/local-fixture.mjs' ? fixture : path.resolve(root, '.' + decodeURIComponent(pathname));
            assert.ok(file === fixture || file.startsWith(root));
            response.setHeader('Content-Type', file.endsWith('.css') ? 'text/css' : 'text/javascript');
            response.end(await readFile(file));
        }
        catch
        {
            response.statusCode = 404;
            response.end();
        }
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    origin = `http://127.0.0.1:${server.address().port}`;
});
after(async() =>
{
    await new Promise(resolve =>
    {
        server.close(resolve);
    });
});

/** 実画面を配信し、Hostの管理境界を観測可能なファイル一覧へ置き換えます。
 * @returns ブラウザーと呼出し・発見状態です。
 */
async function openFilesAsync(context, engine, mode)
{
    const browser = await engine.launch({ headless: true });
    context.after(() => browser.close());
    const page = await browser.newPage({ viewport: { width: 1200, height: 850 } });
    page.setDefaultTimeout(10000);
    const peer = { peerId: 'peer', instanceId: crypto.randomUUID(), hostId: 'test-host', status: 'ready' };
    const state = { peer, calls: [], outcome: 'succeeded', pending: null, entries: new Map() };
    for (const [name, isDirectory] of [['keep.txt', false], ['remove.txt', false], ['folder', true]])
    {
        state.entries.set(name, { name, path: 'C:/files/' + name, isDirectory, length: 1 });
    }
    await page.route('**/client/**', async route =>
    {
        const clusters = route.request().url().endsWith('/clusters');
        await route.fulfill({ json: clusters ? [{ clusterId: 'cluster', peers: [state.peer] }]
            : { jobs: [], totalJobs: 0, offset: 0, unconfirmedPeers: 0 } });
    });
    await page.route('**/api/**', async route =>
    {
        const request = route.request(), body = request.postDataJSON();
        if (request.url().endsWith('/browse'))
        {
            await route.fulfill({ json: { path: body.path, parent: null, entries: body.path ? [...state.entries.values()] : [], nextOffset: null } });
            return;
        }
        state.calls.push(body);
        assert.equal(request.headers()['x-contents-instance'], peer.instanceId);
        assert.equal(body.targetInstanceId, peer.instanceId);
        assert.equal(body.targetPeerId, peer.peerId);
        assert.ok(request.headers()['x-contents-request']);
        await state.pending;
        const outcome = state.failAt === state.calls.length ? 'unknown' : state.outcome;
        if (outcome === 'succeeded')
        {
            const name = body.node.settings.path.split('/').at(-1);
            if (body.node.type === 'directory.create')
            {
                state.entries.set(name, { name, path: body.node.settings.path, isDirectory: true, length: 0 });
            }
            else
            {
                state.entries.delete(name);
            }
        }
        await route.fulfill({ json: { outcome, message: outcome === 'unknown' ? 'Operation unconfirmed. Refresh before repeating.' : null } });
    });
    await page.goto(origin);
    await page.evaluate(async mode =>
    {
        window.fileTool = await import('/remote-tools.js');
        await window.fileTool.mount(document.querySelector('#tool'), mode, 'en');
    }, mode);
    return { page, state };
}

/** パス欄を使い、一覧の取得完了まで待ちます。
 * @returns 対象フォルダーの閲覧完了です。
 */
async function openDirectoryAsync(pane)
{
    const input = pane.locator('.file-location input');
    await input.fill('C:/files');
    await input.press('Enter');
    await pane.getByRole('button', { name: 'keep.txt', exact: true }).waitFor();
}

for (const [name, engine] of [['Chrome', chromium], ['Firefox', firefox]])
{
    test(`${name}: Cluster panes confirm creation and recursive multi-delete, then refresh both copies of the folder`, async context =>
    {
        const { page, state } = await openFilesAsync(context, engine, 'files');
        const left = page.locator('.file-pane').nth(0), right = page.locator('.file-pane').nth(1);
        assert.equal(await left.getByRole('button', { name: 'New folder', exact: true }).isDisabled(), true);
        await left.locator('select').selectOption('test-host');
        await right.locator('select').selectOption('test-host');
        await openDirectoryAsync(left);
        await openDirectoryAsync(right);
        assert.equal(await left.getByRole('button', { name: 'Delete selected', exact: true }).isDisabled(), true);
        await left.getByRole('button', { name: 'New folder', exact: true }).click();
        const dialog = page.getByRole('dialog');
        assert.match(await dialog.textContent(), /test-host · C:\/files/);
        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        await dialog.waitFor({ state: 'detached' });
        assert.equal(state.calls.length, 0);
        await left.getByRole('button', { name: 'New folder', exact: true }).click();
        await dialog.getByLabel('Folder name').fill('../outside');
        await dialog.getByRole('button', { name: 'Create folder', exact: true }).click();
        assert.equal(await dialog.isVisible(), true);
        assert.equal(state.calls.length, 0);
        await dialog.getByLabel('Folder name').fill('日本語 folder');
        await dialog.getByRole('button', { name: 'Create folder', exact: true }).click();
        await right.getByRole('button', { name: '日本語 folder', exact: true }).waitFor();
        assert.equal(state.calls.length, 1);
        await left.getByRole('button', { name: 'remove.txt', exact: true }).click();
        await left.locator('.file-row').filter({ has: page.getByRole('button', { name: 'folder', exact: true }) }).getByRole('checkbox').check();
        await left.getByRole('button', { name: 'Delete selected', exact: true }).click();
        assert.match(await dialog.textContent(), /remove.txt/);
        assert.match(await dialog.textContent(), /folder\//);
        assert.match(await dialog.textContent(), /not be moved to the recycle bin/);
        assert.equal(await dialog.getByRole('button', { name: 'Cancel', exact: true }).evaluate(element => element === document.activeElement), true);
        await page.keyboard.press('Escape');
        await dialog.waitFor({ state: 'detached' });
        assert.equal(state.calls.length, 1);
        const pending = Promise.withResolvers();
        state.pending = pending.promise;
        try
        {
            await left.getByRole('button', { name: 'Delete selected', exact: true }).click();
            await dialog.getByRole('button', { name: 'Delete permanently', exact: true }).click();
            assert.equal(await left.locator('select').isDisabled(), true);
            assert.equal(await right.getByRole('button', { name: 'New folder', exact: true }).isDisabled(), true);
            assert.equal(await page.getByRole('button', { name: 'Refresh hosts', exact: true }).isDisabled(), true);
        }
        finally
        {
            pending.resolve();
        }
        await right.getByRole('button', { name: 'remove.txt', exact: true }).waitFor({ state: 'detached' });
        await page.waitForFunction(() => document.querySelector('.remote-message').textContent === 'Deleted 2 of 2 items.');
        assert.deepEqual(state.calls.slice(1).map(call => call.node.type), ['file.delete', 'directory.delete']);
        assert.equal(state.calls[2].node.settings.recursive, true);
        assert.deepEqual([...state.entries.keys()].sort(), ['keep.txt', '日本語 folder']);
        assert.equal(await left.getByRole('button', { name: 'Delete selected', exact: true }).isDisabled(), true);
    });

    test(`${name}: Browser host stops on an unknown deletion and rejects a restarted host without replay`, async context =>
    {
        const { page, state } = await openFilesAsync(context, engine, 'browser-files');
        const host = page.locator('.file-pane').nth(1);
        await page.getByLabel('Host', { exact: true }).selectOption('test-host');
        await openDirectoryAsync(host);
        for (const entry of ['remove.txt', 'folder', 'keep.txt'])
        {
            await host.locator('.file-row').filter({ has: page.getByRole('button', { name: entry, exact: true }) }).getByRole('checkbox').check();
        }
        state.failAt = 2;
        await host.getByRole('button', { name: 'Delete selected', exact: true }).click();
        await page.getByRole('dialog').getByRole('button', { name: 'Delete permanently', exact: true }).click();
        await host.getByRole('button', { name: 'remove.txt', exact: true }).waitFor({ state: 'detached' });
        await page.waitForFunction(() => document.querySelector('.remote-message').textContent.includes('1 deleted. Operation unconfirmed'));
        assert.equal(state.calls.length, 2);
        assert.equal(state.entries.has('keep.txt'), true);
        await host.getByRole('button', { name: 'New folder', exact: true }).click();
        state.peer = { ...state.peer, instanceId: crypto.randomUUID() };
        await page.getByRole('dialog').getByLabel('Folder name').fill('stale');
        await page.getByRole('dialog').getByRole('button', { name: 'Create folder', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.remote-message').textContent.includes('selected PC changed'));
        assert.equal(state.calls.length, 2);
    });

    test(`${name}: local folder mutations require a directory handle and cancel when the screen closes`, async context =>
    {
        const { page, state } = await openFilesAsync(context, engine, 'browser-files');
        const local = page.locator('.file-pane').nth(0);
        assert.equal(await local.getByRole('button', { name: 'New folder', exact: true }).isDisabled(), true);
        await page.evaluate(async() =>
        {
            const { LocalFolder, LocalFile } = await import('/local-fixture.mjs');
            window.localRoot = new LocalFolder('chosen', [new LocalFile('delete.txt'), new LocalFolder('contents', [new LocalFile('nested')])]);
            window.showDirectoryPicker = async() => window.localRoot;
            await window.fileTool.mount(document.querySelector('#tool'), 'browser-files', 'en');
        });
        await local.getByRole('button', { name: 'Choose local folder', exact: true }).click();
        await local.getByRole('button', { name: 'delete.txt', exact: true }).waitFor();
        await local.getByRole('button', { name: 'New folder', exact: true }).click();
        await page.getByRole('dialog').getByLabel('Folder name').fill('created');
        await page.getByRole('dialog').getByRole('button', { name: 'Create folder', exact: true }).click();
        await local.getByRole('button', { name: 'created', exact: true }).waitFor();
        for (const name of ['delete.txt', 'contents'])
        {
            await local.locator('.file-row').filter({ has: page.getByRole('button', { name, exact: true }) }).getByRole('checkbox').check();
        }
        await local.getByRole('button', { name: 'Delete selected', exact: true }).click();
        await page.getByRole('dialog').getByRole('button', { name: 'Delete permanently', exact: true }).click();
        await local.getByRole('button', { name: 'delete.txt', exact: true }).waitFor({ state: 'detached' });
        assert.deepEqual(await page.evaluate(() => [...window.localRoot.children.keys()]), ['created']);
        await local.getByRole('button', { name: 'New folder', exact: true }).click();
        await page.getByRole('dialog').getByLabel('Folder name').fill('cancelled');
        await page.evaluate(() => window.fileTool.unmount(document.querySelector('#tool')));
        assert.deepEqual(await page.evaluate(() => [...window.localRoot.children.keys()]), ['created']);
        assert.equal(state.calls.length, 0);
    });
}
