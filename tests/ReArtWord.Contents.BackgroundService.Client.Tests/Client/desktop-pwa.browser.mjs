import { before, after, test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';

// 公開済みの静的資材だけをloopbackで配信し、管理要求は試験内で応答します。
assert.ok(process.env.CONTENTS_PWA_WEBROOT, 'Set CONTENTS_PWA_WEBROOT to the published ClientHost wwwroot.');
const root = path.resolve(process.env.CONTENTS_PWA_WEBROOT);
let server, origin;

before(async () =>
{
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.wasm': 'application/wasm', '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.png': 'image/png', '.svg': 'image/svg+xml' };
    const management = {
        '/client/health': { buildVersion: 'pwa-test' },
        '/client/branding': { contentsName: 'Clutch-CS', subtitle: 'WebClient' },
        '/client/clusters': [],
        '/client/network': { interfaces: [], bindings: [] },
        '/client/updates/visible': { owned: [], observed: [] },
        '/client/transfers/active-count': 0,
        '/client/transfers/state': { jobs: [], totalJobs: 0, unconfirmedPeers: 0 }
    };
    server = createServer(async (request, response) =>
    {
        try
        {
            const pathname = new URL(request.url, 'http://localhost').pathname;
            if (pathname.startsWith('/client/') || pathname.startsWith('/api/'))
            {
                response.setHeader('Content-Type', 'application/json');
                response.setHeader('Cache-Control', 'no-store');
                response.end(JSON.stringify(management[pathname] ?? null));
                return;
            }
            const asset = pathname === '/' ? '/ui-start.html' : (path.extname(pathname) ? pathname : '/index.html');
            const file = path.resolve(root, '.' + decodeURIComponent(asset));
            assert.ok(file.startsWith(root + path.sep));
            response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
            response.setHeader('Cache-Control', 'no-cache');
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

after(async () =>
{
    await new Promise(resolve =>
    {
        server?.close(resolve);
    });
});

test('published Desktop PWA is installable from the current origin and reopens its shell offline', { timeout: 120000 }, async () =>
{
    const temporary = path.resolve(tmpdir()), profile = await mkdtemp(path.join(temporary, 'contents-pwa-'));
    let context;
    try
    {
        context = await chromium.launchPersistentContext(profile, { headless: true, channel: process.env.CONTENTS_PWA_BROWSER });
        const page = await context.newPage();
        page.setDefaultTimeout(60000);
        const errors = [];
        page.on('pageerror', error => errors.push(String(error)));
        page.on('console', message =>
        {
            if (message.type() === 'error')
            {
                errors.push(message.text());
            }
        });
        await page.goto(origin + '/');
        await page.waitForURL('**/dashboard');
        await page.locator('.navigation').waitFor();
        await page.waitForFunction(async () => (await window.contentsBrowser?.pwaStatus())?.ready === true);
        await page.reload();
        await page.waitForFunction(() => navigator.serviceWorker.controller !== null);

        const protocol = await context.newCDPSession(page);
        const manifest = await protocol.send('Page.getAppManifest');
        assert.deepEqual(manifest.errors, []);
        assert.equal(manifest.url, origin + '/manifest.webmanifest');
        const app = JSON.parse(manifest.data);
        assert.equal(app.name, 'Clutch-CS WebClient');
        assert.equal(app.display, 'standalone');
        for (const property of ['id', 'scope', 'start_url'])
        {
            assert.equal(new URL(app[property], manifest.url).href, origin + '/');
        }
        const installability = await protocol.send('Page.getInstallabilityErrors');
        assert.deepEqual(installability.installabilityErrors, []);

        const response = await context.request.get(manifest.url);
        assert.equal(response.status(), 200);
        assert.match(response.headers()['content-type'], /^application\/manifest\+json/);
        for (const icon of app.icons)
        {
            const dimensions = await page.evaluate(async source =>
            {
                const image = new Image();
                image.src = source;
                await image.decode();
                return `${image.naturalWidth}x${image.naturalHeight}`;
            }, new URL(icon.src, manifest.url).href);
            assert.equal(dimensions, icon.sizes);
        }
        assert.deepEqual(app.icons.map(icon => icon.sizes).sort(), ['192x192', '512x512']);

        await page.evaluate(() => sessionStorage.setItem('contents.pwa.test-draft', 'preserved'));
        await context.setOffline(true);
        for (const path of ['/manifest.webmanifest', ...app.icons.map(icon => '/' + icon.src)])
        {
            assert.equal(await page.evaluate(async resource => (await fetch(resource)).ok, path), true);
        }
        for (const path of ['/dashboard', '/editor', '/host-manager', '/file-transfer', '/browser-transfer', '/terminal', '/desktop', '/signal-scope', '/logs', '/licenses'])
        {
            await page.goto(origin + path);
            await page.locator('.navigation').waitFor().catch(async error =>
            {
                throw new Error(`${path}: ${await page.locator('body').innerText()} / ${errors.join('; ')}`, { cause: error });
            });
            assert.equal(await page.evaluate(() => sessionStorage.getItem('contents.pwa.test-draft')), 'preserved');
        }
        assert.equal(await page.evaluate(async () =>
        {
            try
            {
                await fetch('/client/health');
                return false;
            }
            catch
            {
                return true;
            }
        }), true);

        await context.setOffline(false);
        await page.goto(origin + '/ui-update.html');
        const recoveryManifest = await protocol.send('Page.getAppManifest');
        assert.equal(recoveryManifest.url, manifest.url);
        await page.getByRole('button', { name: /Update/ }).click();
        await page.waitForURL('**/dashboard');
        await page.locator('.navigation').waitFor();
        assert.equal(await page.evaluate(() => sessionStorage.getItem('contents.pwa.test-draft')), 'preserved');
    }
    finally
    {
        await context?.close();
        assert.equal(path.dirname(profile), temporary);
        assert.ok(path.basename(profile).startsWith('contents-pwa-'));
        await rm(profile, { recursive: true, force: true });
    }
});
