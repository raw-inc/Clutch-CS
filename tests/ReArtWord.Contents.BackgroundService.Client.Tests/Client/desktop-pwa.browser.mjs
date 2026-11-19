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
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.wasm': 'application/wasm', '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.woff2': 'font/woff2', '.png': 'image/png', '.svg': 'image/svg+xml' };
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
        context = await chromium.launchPersistentContext(profile, { headless: true, viewport: { width: 1440, height: 900 }, channel: process.env.CONTENTS_PWA_BROWSER });
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

        await page.emulateMedia({ colorScheme: 'light' });
        await page.getByRole('tab', { name: 'Settings & maintenance' }).click();
        const settingsTab = page.getByRole('tab', { name: 'Settings & maintenance' });
        await settingsTab.focus();
        await page.keyboard.press('Home');
        await page.waitForFunction(() => document.activeElement?.textContent?.includes('All clusters'));
        await page.keyboard.press('End');
        await page.waitForFunction(() => document.activeElement?.textContent?.includes('Settings & maintenance'));
        const theme = page.getByLabel('Appearance');
        await theme.selectOption('dark');
        await page.getByRole('button', { name: 'Save display settings' }).click();
        await page.waitForFunction(() => getComputedStyle(document.documentElement).colorScheme === 'dark');
        await page.evaluate(() => sessionStorage.setItem('contents.pwa.test-draft', 'preserved'));
        await context.setOffline(true);
        for (const path of ['/manifest.webmanifest', '/icons/controls.svg', '/vendor/fonts/roboto-latin.woff2', '/vendor/fonts/m-plus-2.woff2', '/vendor/fonts/zen-kaku-gothic-new-bold.woff2', ...app.icons.map(icon => '/' + icon.src)])
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
            await page.waitForFunction(() => document.querySelector('.navigation svg')?.getBBox().width && Array.from(document.querySelectorAll('.navigation svg')).every(icon => 0 < icon.getBBox().width));
            assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).backgroundColor), 'rgb(17, 17, 17)');
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

        await page.setViewportSize({ width: 390, height: 844 });
        const picker = page.locator('.route-picker select');
        await picker.selectOption('logs');
        await page.waitForURL('**/logs');
        const recent = page.getByRole('switch', { name: 'Search recent history only' });
        assert.ok(await recent.isChecked());
        await recent.uncheck();
        assert.equal(await recent.isChecked(), false);
        await picker.selectOption('dashboard');
        await page.waitForURL('**/dashboard');
        await page.locator('.compact-view select').selectOption('Settings & maintenance');
        assert.ok(await page.getByLabel('Appearance').isVisible());
        await page.goto(origin + '/licenses');
        await page.getByRole('searchbox').fill('Microsoft.Extensions.Configuration.EnvironmentVariables');
        await page.locator('.license-list section:visible').first().waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), 390);

        await context.setOffline(false);
        await page.goto(origin + '/ui-update.html');
        assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), 'dark');
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

        // Windows ではブラウザーを閉じた直後もログファイルが使用中のことがある。
        await rm(profile, { recursive: true, force: true, maxRetries: 5 });
    }
});

test('配色の保存と解除が別タブにも反映され、保存できない場合も端末の設定で開ける', async () =>
{
    const browser = await chromium.launch();
    try
    {
        const context = await browser.newContext({ colorScheme: 'dark' });
        const settings = await context.newPage(), update = await context.newPage();
        await settings.goto(origin + '/dashboard');
        await settings.getByRole('tab', { name: 'Settings & maintenance' }).click();
        await update.goto(origin + '/ui-update.html');
        const picker = settings.getByLabel('Appearance');
        for (const [choice, background] of [['light', 'rgb(245, 245, 242)'], ['system', 'rgb(17, 17, 17)']])
        {
            await picker.selectOption(choice);
            await settings.getByRole('button', { name: 'Save display settings' }).click();
            await update.waitForFunction(color => getComputedStyle(document.documentElement).backgroundColor === color, background);
        }
        await update.emulateMedia({ colorScheme: 'light' });
        assert.equal(await update.evaluate(() => getComputedStyle(document.documentElement).backgroundColor), 'rgb(245, 245, 242)');
        await context.close();

        const restricted = await browser.newPage({ colorScheme: 'dark' });
        await restricted.addInitScript(() => Object.defineProperty(window, 'localStorage', {
            /** 保存領域を読み取れないブラウザーを再現します。 */
            get()
            {
                throw new DOMException('Storage unavailable', 'SecurityError');
            } }));
        await restricted.goto(origin + '/ui-update.html');
        assert.equal(await restricted.evaluate(() => window.contentsTheme.get()), 'system');
        assert.equal(await restricted.evaluate(() => getComputedStyle(document.documentElement).backgroundColor), 'rgb(17, 17, 17)');
    }
    finally
    {
        await browser.close();
    }
});

test('published conflict cards keep their heading, explanation and action together at desktop and mobile widths', async context =>
{
    const browser = await chromium.launch();
    context.after(() => browser.close());
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block' });
    await page.addInitScript(() =>
    {
        sessionStorage.setItem('contents.client.cluster', JSON.stringify('studio'));
        sessionStorage.setItem('contents.client.peer', JSON.stringify('review'));
    });
    await page.goto(origin + '/dashboard');
    await page.getByRole('tab', { name: 'Resolve conflicts' }).click();
    for (const width of [1440, 390, 320])
    {
        await page.setViewportSize({ width, height: 900 });
        const headings = page.locator('.dashboard-operations:visible section h2');
        assert.equal(await headings.count(), 2);
        for (const title of await headings.all())
        {
            const boxes = await title.evaluate(heading =>
            {
                const section = heading.closest('section');
                return [heading, section.querySelector('p'), section.querySelector('button'), section].map(e => e.getBoundingClientRect().toJSON());
            });
            const [heading, help, action, section] = boxes;
            assert.ok(heading.bottom <= help.top && action.right <= section.right);
            if (width === 1440)
            {
                assert.ok(help.right < action.left);
                assert.ok(Math.abs(heading.top + help.bottom - action.top - action.bottom) <= 2);
            }
            else
            {
                assert.ok(help.bottom < action.top);
                assert.ok(Math.abs(heading.left - action.left) <= 1);
            }
        }
    }
});

test('keyboard focus stays visible inside a field without shifting it; forced colors retain a focus indicator', async context =>
{
    const browser = await chromium.launch();
    context.after(() => browser.close());
    const page = await browser.newPage({ serviceWorkers: 'block' });
    await page.goto(origin + '/licenses');
    const field = page.getByRole('searchbox');
    await field.waitFor();
    for (const colorScheme of ['light', 'dark'])
    {
        await page.emulateMedia({ colorScheme });
        await field.evaluate(e => e.blur());
        const before = await field.boundingBox();
        await field.focus();
        const mark = await field.evaluate(e =>
        {
            const s = getComputedStyle(e);
            return [s.outlineStyle, s.boxShadow, s.borderBottomColor];
        });
        assert.equal(mark[0], 'none');
        assert.ok(mark[1].includes('inset') && mark[1].includes(mark[2]));
        assert.deepEqual(await field.boundingBox(), before);
    }
    await page.emulateMedia({ forcedColors: 'active' });
    assert.notEqual(await field.evaluate(e => getComputedStyle(e).outlineStyle), 'none');
});

test('touch footer actions remain reachable at narrow and wide viewport sizes', async context =>
{
    const browser = await chromium.launch();
    context.after(() => browser.close());
    const page = await browser.newPage({ hasTouch: true, viewport: { width: 390, height: 844 }, serviceWorkers: 'block' });
    await page.addInitScript(() =>
    {
        sessionStorage.setItem('contents.client.cluster', JSON.stringify('studio'));
        sessionStorage.setItem('contents.client.peer', JSON.stringify('review'));
    });
    await page.goto(origin + '/dashboard');
    await page.locator('.statusbar-action').waitFor();
    for (const width of [390, 1280])
    {
        await page.setViewportSize({ width, height: 844 });
        const sizes = await page.locator('.statusbar-left').evaluate(e =>
        {
            const footer = e.closest('footer').getBoundingClientRect();
            return [...e.querySelectorAll('a, button')].filter(n => n.checkVisibility()).map(n =>
            {
                const { top, bottom, height } = n.getBoundingClientRect();
                return { height, within: footer.top <= top && bottom <= footer.bottom };
            });
        });
        assert.ok(3 <= sizes.length);
        assert.ok(sizes.every(s => 44 <= s.height && s.within), JSON.stringify(sizes));
    }
});
