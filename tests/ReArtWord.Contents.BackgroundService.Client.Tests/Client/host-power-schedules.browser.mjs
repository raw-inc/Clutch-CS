import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

/** 試験用ブラウザーに、リポジトリの画面資材だけをloopbackで提供します。
 * @returns 所有するサーバーと接続先です。
 */
async function serveAsync(context)
{
    const root = new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url);
    const server = createServer((request, response) =>
    {
        if (request.url === '/')
        {
            response.setHeader('Content-Type', 'text/html');
            response.end('<!doctype html><html lang="ja"><meta charset="utf-8"><link rel="stylesheet" href="/app.css"><main class="page host-manager"></main></html>');
            return;
        }
        const file = new URL('.' + request.url, root);
        if (!file.href.startsWith(root.href) || !/\.(?:js|css)$/u.test(file.pathname))
        {
            response.writeHead(404).end();
            return;
        }
        readFile(file).then(contents =>
        {
            response.setHeader('Content-Type', file.pathname.endsWith('.css') ? 'text/css' : 'text/javascript');
            response.end(contents);
        }).catch(() => response.writeHead(404).end());
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    context.after(() => new Promise(resolve =>
    {
        server.close(resolve);
    }));
    return `http://127.0.0.1:${server.address().port}`;
}

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    test(`${name}: selected hosts can replace, disable and enable schedules without real power operations`, { timeout: 60000 }, async context =>
    {
        const origin = await serveAsync(context), labels = {};
        for (const file of ['HostManager.ja.json', 'HostPowerSchedules.ja.json'])
        {
            Object.assign(labels, JSON.parse(await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/Localization/' + file, import.meta.url), 'utf8')));
        }
        const hosts = ['host-a', 'host-b', 'host-unselected'].map((hostId, index) => ({ target: { hostId, clusterId: index === 1 ? 'beta' : 'alpha' }, capabilities: null }));
        const views = new Map(hosts.map((host, index) => [host.target.hostId, { ...host.target, revision: 'original-' + index, active: true, status: [], wakeProfileAvailable: true,
            schedules: [{ id: 'schedule-' + index, action: 'shutdown', time: '23:00', timeZoneId: 'UTC', weekdays: [], enabled: true }] }]));
        const requests = [], errors = [];
        let failSecond = false;
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        page.on('pageerror', error => errors.push(error.message));
        await page.route('**/client/host-manager/**', async route =>
        {
            const path = new URL(route.request().url()).pathname.split('/').at(-1), request = route.request().postDataJSON();
            let body;
            switch (path)
            {
                case 'hosts':
                    body = hosts;
                    break;
                case 'batches':
                    body = [];
                    break;
                case 'clusters':
                    body = ['alpha', 'beta'];
                    break;
                case 'read':
                    body = views.get(request.hostId);
                    break;
                case 'save':
                    {
                        requests.push(request);
                        if (failSecond && request.hostId === 'host-b')
                        {
                            body = { outcome: 'unknown', reason: 'powerSchedule.saveUnconfirmed' };
                        }
                        else
                        {
                            const view = { ...views.get(request.hostId), schedules: request.schedules, revision: 'saved-' + requests.length };
                            views.set(request.hostId, view);
                            body = { outcome: 'succeeded', view };
                        }
                    }
                    break;
                default:
                    throw new Error('Unexpected test request: ' + path);
            }
            await route.fulfill({ json: body });
        });
        await page.goto(origin);
        await page.evaluate(async translations =>
        {
            const { mount, textKeys } = await import('/host-manager/index.js');
            await mount(document.querySelector('main'), { ...Object.fromEntries(textKeys().map(key => [key, key])), ...translations });
        }, labels);
        await page.getByRole('tab', { name: labels['powerSchedule.title'], exact: true }).click();
        await page.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        await page.getByRole('checkbox', { name: 'host-b', exact: true }).check();
        await page.waitForFunction(label => [...document.querySelectorAll('#hm-panel-powerSchedules button')].filter(button => button.textContent === label && !button.disabled).length === 2, labels['powerSchedule.save']);
        await page.getByRole('button', { name: labels['powerSchedule.bulk'], exact: true }).click();
        const dialog = page.getByRole('dialog');
        await dialog.getByLabel(labels['powerSchedule.time'], { exact: true }).fill('22:30:00');
        await dialog.getByLabel(labels['powerSchedule.zone'], { exact: true }).fill('Asia/Tokyo');
        assert.equal(requests.length, 0);
        await dialog.getByRole('button', { name: labels['powerSchedule.bulkApply'], exact: true }).click();
        await page.waitForFunction(label => [...document.querySelectorAll('dialog td [role="status"]')].filter(item => item.textContent === label).length === 2, labels['powerSchedule.succeeded']);
        assert.deepEqual(requests.map(item => [item.hostId, item.clusterId, item.expectedRevision]), [['host-a', 'alpha', 'original-0'], ['host-b', 'beta', 'original-1']]);
        assert.deepEqual(requests[0].schedules, requests[1].schedules);
        assert.equal(requests[0].schedules[0].time, '22:30:00');
        assert.equal(requests[0].schedules[0].timeZoneId, 'Asia/Tokyo');
        const evidence = new URL('../../../.work/host-power-schedules/', import.meta.url);
        await mkdir(evidence, { recursive: true });
        await page.screenshot({ path: fileURLToPath(new URL('bulk-' + name.toLowerCase() + '.png', evidence)), fullPage: true });
        await page.keyboard.press('Escape');
        await page.getByRole('button', { name: labels['powerSchedule.bulkDisable'], exact: true }).click();
        await dialog.getByRole('button', { name: labels['powerSchedule.bulkApply'], exact: true }).click();
        await page.waitForFunction(label => [...document.querySelectorAll('dialog td [role="status"]')].filter(item => item.textContent === label).length === 2, labels['powerSchedule.succeeded']);
        assert.equal(requests.length, 4);
        assert.deepEqual(requests[2].schedules, requests[0].schedules.map(item => ({ ...item, enabled: false })));
        assert.deepEqual(requests[3].schedules, requests[1].schedules.map(item => ({ ...item, enabled: false })));
        assert.deepEqual(await dialog.locator('tbody tr td:nth-child(3)').allTextContents(), ['0 / 1', '0 / 1']);
        await page.keyboard.press('Escape');
        failSecond = true;
        await page.getByRole('button', { name: labels['powerSchedule.bulkEnable'], exact: true }).click();
        await dialog.getByRole('button', { name: labels['powerSchedule.bulkApply'], exact: true }).click();
        await dialog.locator('td [role="status"]').nth(1).filter({ hasText: labels['powerSchedule.saveUnconfirmed'] }).waitFor();
        assert.equal(requests.length, 6);
        assert.equal(await dialog.getByRole('button', { name: labels['powerSchedule.bulkApply'], exact: true }).isDisabled(), true);
        assert.equal(requests.some(item => item.hostId === 'host-unselected'), false);
        await page.setViewportSize({ width: 390, height: 844 });
        const bounds = await dialog.boundingBox();
        assert.ok(0 <= bounds.x && bounds.x + bounds.width <= 391);
        assert.deepEqual(errors, []);
    });
}
