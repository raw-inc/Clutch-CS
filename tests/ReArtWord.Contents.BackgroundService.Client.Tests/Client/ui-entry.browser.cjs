// Native service-worker installation/activation with real integrity checks and isolated profiles.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const { createHash } = require('node:crypto');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { chromium } = require(process.env.CONTENTS_TEST_PLAYWRIGHT || 'playwright');
const root = join(__dirname, '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot');
const staticFiles = Object.fromEntries(['ui-start.html', 'ui-update.html', 'ui-update.js', 'ui-update.css', 'client-branding.js', 'favicon.svg'].map(name => [name, readFileSync(join(root, name))]));
const worker = readFileSync(join(root, 'service-worker.published.js'));

test('root UI entry', async t =>
{
    let version = 'one', broken = false, unavailable = false;
    const requests = [];

    /** 試験に必要な配信資材を用意します。
     * @returns 呼出し元が使用する値です。
     */
    function assets(revision)
    {
        const values = { ...staticFiles, 'index.html': Buffer.from(`<title>Dashboard</title><h1 id="version">${revision}</h1>`) };
        for (let i = 0; i < 12; i++)
        {
            values[`chunk-${i}.json`] = Buffer.from(JSON.stringify({ revision, i }));
        }
        return values;
    }

    const server = createServer((request, response) =>
    {
        const url = new URL(request.url, 'http://localhost'), path = url.pathname;
        requests.push({ path, version: url.searchParams.get('__ui') });
        response.setHeader('Cache-Control', 'no-store');
        const revision = url.searchParams.get('__ui') || url.searchParams.get('ui') || version;
        let body;
        switch (path)
        {
            case '/client/health':
                {
                    response.setHeader('Content-Type', 'application/json');
                    response.statusCode = unavailable ? 503 : 200;
                    body = JSON.stringify({ buildVersion: version });
                }
                break;
            case '/client/branding':
                {
                    response.setHeader('Content-Type', 'application/json');
                    body = JSON.stringify({ contentsName: 'Contents' });
                }
                break;
            case '/service-worker.js':
                {
                    response.setHeader('Content-Type', 'text/javascript');
                    body = worker;
                }
                break;
            case '/service-worker-assets.js':
                {
                    response.setHeader('Content-Type', 'text/javascript');
                    const entries = Object.entries(assets(revision)).map(([url, bytes]) => ({ url, hash: 'sha256-' + createHash('sha256').update(bytes).digest('base64') }));
                    body = 'self.assetsManifest=' + JSON.stringify({ version: revision, assets: entries });
                }
                break;
            default:
                {
                    const name = path === '/' ? 'ui-start.html' : ['/dashboard', '/editor'].includes(path) ? 'index.html' : path.slice(1);
                    body = assets(revision)[name];
                    if (!body)
                    {
                        response.statusCode = 404;
                        response.end();
                        return;
                    }
                    response.setHeader('Content-Type', name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : name.endsWith('.json') ? 'application/json' : name.endsWith('.svg') ? 'image/svg+xml' : 'text/html');
                    if (broken && name === 'chunk-0.json')
                    {
                        body = 'integrity failure';
                    }
                }
                break;
        }
        if (path.startsWith('/chunk-'))
        {
            setTimeout(() => response.end(body), 180);
        }
        else
        {
            response.end(body);
        }
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    t.after(() => new Promise(resolve =>
    {
        server.closeAllConnections();
        server.close(resolve);
    }));
    const browser = await chromium.launch({ channel: process.env.CONTENTS_TEST_BROWSER || 'msedge', headless: true });
    t.after(() => browser.close());
    const base = `http://127.0.0.1:${server.address().port}`;
    const context = await browser.newContext({ viewport: { width: 960, height: 600 } });
    t.after(() => context.close());
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));

    /** 試験用のDashboard応答を構成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function dashboardAsync(expected)
    {
        await page.waitForURL(base + '/dashboard');
        assert.equal(await page.locator('#version').textContent(), expected);
    }

    await t.test('first visit installs verified assets, shows slim progress and reaches Dashboard', async () =>
    {
        await page.goto(base + '/');
        await page.locator('#splash').waitFor({ state: 'visible' });
        assert.equal(await page.title(), 'Clutch-CS');
        await page.waitForFunction(() => (0) < (document.querySelector('progress')?.value));
        const progress = await page.locator('progress').evaluate(element => ({ height: element.getBoundingClientRect().height, value: element.value, max: element.max }));
        assert.equal(progress.height, 3);
        assert((0) < (progress.value) && progress.value <= progress.max);
        if (process.env.CONTENTS_TEST_UI_ENTRY_SCREENSHOT)
        {
            await page.screenshot({ path: process.env.CONTENTS_TEST_UI_ENTRY_SCREENSHOT });
        }
        await dashboardAsync('one');
        assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).waiting), null);
    });

    await t.test('unchanged assets skip download and go directly to Dashboard', async () =>
    {
        const downloaded = requests.filter(r => r.path.startsWith('/chunk-')).length;
        await page.goto(base + '/');
        await dashboardAsync('one');
        assert.equal(requests.filter(r => r.path.startsWith('/chunk-')).length, downloaded);
    });

    await t.test('a newer version is fully installed and activated on a solo root visit', async () =>
    {
        version = 'two';
        await page.goto(base + '/');
        await dashboardAsync('two');
        assert.equal(await page.evaluate(() => caches.keys()).then(keys => keys.includes('contents-ui-one')), false);
    });

    await t.test('another open editor defers activation but never blocks automatic navigation', async () =>
    {
        const editor = await context.newPage();
        await editor.goto(base + '/editor');
        version = 'three';
        await page.goto(base + '/');
        await dashboardAsync('two');
        assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).waiting?.state), 'installed');
        assert.equal(await editor.locator('#version').textContent(), 'two');
        await editor.close();
        await page.goto(base + '/');
        await dashboardAsync('three');
    });

    await t.test('manual update retains its explicit blocked message and retry', async () =>
    {
        const editor = await context.newPage();
        await editor.goto(base + '/editor');
        version = 'four';
        await page.goto(base + '/ui-update.html');
        await page.locator('#update').click();
        await page.waitForFunction(() => document.querySelector('#status').textContent.includes('Close the other tabs'));
        assert.equal(new URL(page.url()).pathname, '/ui-update.html');
        await editor.close();
        await page.locator('#update').click();
        await dashboardAsync('four');
    });

    await t.test('failed asset integrity keeps the old UI and still reaches Dashboard', async () =>
    {
        version = 'broken';
        broken = true;
        await page.goto(base + '/');
        await dashboardAsync('four');
        assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).waiting), null);
        broken = false;
        version = 'four';
    });

    await t.test('an unavailable host or offline connection still opens cached Dashboard', async () =>
    {
        unavailable = true;
        await page.goto(base + '/');
        await dashboardAsync('four');
        unavailable = false;
        await context.setOffline(true);
        try
        {
            await page.goto(base + '/');
            await dashboardAsync('four');
        }
        finally
        {
            await context.setOffline(false);
        }
    });

    await t.test('a browser without service-worker support still navigates', async () =>
    {
        const unsupported = await browser.newContext();
        try
        {
            await unsupported.addInitScript(() => Object.defineProperty(Navigator.prototype, 'serviceWorker', { value: undefined }));
            const plain = await unsupported.newPage();
            await plain.goto(base + '/');
            await plain.waitForURL(base + '/dashboard');
            assert.equal(await plain.locator('#version').textContent(), 'four');
        }
        finally
        {
            await unsupported.close();
        }
    });
    assert.deepEqual(errors, []);
});
