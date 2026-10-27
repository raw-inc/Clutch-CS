import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium, firefox } from 'playwright';
import { WebSocketServer } from 'ws';

const root = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url));
let server, origin, webSockets, connectFixture;
before(async () =>
{
    server = createServer(async (request, response) =>
    {
        try
        {
            const pathname = new URL(request.url, 'http://localhost').pathname;
            if (pathname === '/')
            {
                response.setHeader('Content-Type', 'text/html; charset=utf-8');
                response.end('<!doctype html><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/signal-scope.css"><main id="scope" class="page"></main>');
                return;
            }
            const file = path.resolve(root, '.' + decodeURIComponent(pathname));
            assert(file.startsWith(path.resolve(root) + path.sep));
            response.setHeader('Content-Type', file.endsWith('.css') ? 'text/css' : 'text/javascript');
            response.end(await readFile(file));
        }
        catch
        {
            response.statusCode = 404;
            response.end();
        }
    });
    webSockets = new WebSocketServer({ server });
    webSockets.on('connection', (socket, request) => connectFixture(socket, request));
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () =>
{
    for (const socket of webSockets.clients)
    {
        socket.terminate();
    }
    await new Promise(resolve =>
    {
        webSockets.close(resolve);
    });
    await new Promise(resolve =>
    {
        server.close(resolve);
    });
});

/** ブラウザーへ中継されたkind付きフレームを作ります。
 * @returns 制御または原本付きのバイナリです。
 */
function frame(value, raw, kind = 2)
{
    const json = Buffer.from(JSON.stringify(value));
    if (!raw)
    {
        return Buffer.concat([Buffer.from([1]), json]);
    }
    const header = Buffer.alloc(5);
    header[0] = kind;
    header.writeUInt32BE(json.length, 1);
    return Buffer.concat([header, json, raw]);
}

/** OSや実NICを触らず、実Web Workerと実DOMへ有限の通信を流します。
 * @returns 観測した開始・停止要求と切断です。
 */
async function mountAsync(page, experimental)
{
    const commands = [], closed = [], sockets = new Map();
    connectFixture = (socket, request) =>
    {
        const host = new URL(request.url, origin).searchParams.get('peer');
        const packets = new Map();
        sockets.set(host, socket);
        socket.on('close', () => closed.push(host));
        socket.on('message', raw =>
        {
            const message = JSON.parse(String(raw));
            commands.push({ host, ...message });
            if (message.type === 'list')
            {
                socket.send(frame({ type: 'catalog', adapters: [{ backend: 'pcap', id: 'nic', name: 'Test NIC', addresses: [host === 'host-a' ? '192.168.10.10' : '192.168.20.10'], details: 'Fixture' }], errors: [] }));
            }
            if (message.type === 'start')
            {
                socket.send(frame({ type: 'state', running: true, sources: [{ backend: 'pcap', adapterId: 'nic', running: true, error: '' }] }));
                for (let index = 0; index < 200; index++)
                {
                    const packet = { packetId: index + 1, queryId: 0, matched: true, sourceId: 'pcap:nic', observationPoint: 'rx', fingerprint: String(index), time: '2025-01-02T04:00:00.000Z', linkType: 1, originalLength: 10,
                        source: '192.168.10.10', destination: '192.168.20.10', sourcePort: 9001, destinationPort: 9000, protocol: 'OSC', transport: 'UDP',
                        summary: '/address/to ' + index + (index === 42 ? ' ready and or' : ' different') };
                    packets.set(index + 1, packet);
                    socket.send(frame(packet, Buffer.alloc(0)));
                }
            }
            if (message.type === 'configure')
            {
                socket.send(frame({ type: 'configured' }));
            }
            if (message.type === 'detail')
            {
                const packet = packets.get(message.packetId);
                if (packet)
                {
                    socket.send(frame({ ...packet, requestId: message.requestId, details: 'OSC packet ' + (message.packetId - 1) + ' deep-only' }, Buffer.from('test bytes'), 3));
                }
                else
                {
                    socket.send(frame({ type: 'detail', ...message, removed: true }));
                }
            }
            if (message.type === 'search')
            {
                const target = message.mode === 'hex' ? '74657374206279746573' : 'deep-only';
                const match = !message.expression.term || target.includes(message.expression.term);
                socket.send(frame({ type: 'search', requestId: message.requestId, ids: match ? message.mode === 'hex' ? [43] : [43, 201] : [], reset: true, complete: true }));
            }
            if (message.type === 'clear')
            {
                packets.clear();
                socket.send(frame({ type: 'cleared' }));
            }
            if (message.type === 'stop')
            {
                socket.send(frame({ type: 'state', running: false, sources: [] }));
            }
            if (message.type === 'exclusions')
            {
                socket.send(frame({ type: 'exclusions' }));
            }
        });
        socket.send(frame({ type: 'transport', endpoints: [{ address: '127.0.0.1', port: host === 'host-a' ? 61001 : 61002 }] }));
        socket.send(frame({ type: 'hello', protocolVersion: 2 }));
    };
    await page.goto(origin + (experimental ? '/?experimental=true' : '/'));
    await page.evaluate(async () =>
    {
        const { signalScopeViewAsync } = await import('/signal-scope.js');
        window.scopeErrors = [];
        window.scopeLifetime = new AbortController();
        window.scope = await signalScopeViewAsync(document.querySelector('#scope'), { signal: window.scopeLifetime.signal, timeZone: 'UTC',

            t: en => en,

            say: message => window.scopeErrors.push(message),

            peers: async () => [
                { hostId: 'host-a', peerId: 'stale-a', clusterId: 'other', instanceId: 'old', address: '192.0.2.3', status: 'unavailable' },
                ...['host-a', 'host-b'].map((host, index) => ({ hostId: host, peerId: host, clusterId: 'test', instanceId: String(index), address: '192.0.2.' + (index + 1), status: 'ready' })),
                { hostId: 'host-a', peerId: 'duplicate-a', clusterId: 'another', instanceId: 'another', address: '192.0.2.4', status: 'ready' }],

            button: (text, action) =>
            {
                const button = document.createElement('button');
                button.textContent = text;
                button.onclick = action;
                return button;
            } });
    });
    return { commands, closed, sockets };
}

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    test(`${name}: saved experimental preference applies without a query and can be disabled`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage();
        await page.goto(origin);
        await page.evaluate(async () => (await import('/experimental.js')).saveExperimentalPreference(true));
        await mountAsync(page, false);
        assert.equal(await page.getByText('Advanced Search', { exact: true }).count(), 1);
        assert.equal(await page.getByText('Capture limits', { exact: true }).count(), 1);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
            (await import('/experimental.js')).saveExperimentalPreference(false);
        });
        await mountAsync(page, false);
        assert.equal(await page.getByText('Advanced Search', { exact: true }).count(), 0);
        assert.equal(await page.getByText('Capture limits', { exact: true }).count(), 0);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });

    test(`${name}: host aggregation and independent folding preserve selected capture sources`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        page.setDefaultTimeout(4000);
        const { commands, sockets } = await mountAsync(page, false);
        assert.equal(await page.locator('.scope-host').count(), 2);
        const group = page.locator('.scope-host[data-host="host-a"]');
        await group.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        const adapter = group.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true, includeHidden: true });
        await adapter.check();
        const heading = group.locator('summary');
        await heading.click({ position: { x: 4, y: 10 } });
        assert.equal(await adapter.isVisible(), false);
        assert.equal(await adapter.isChecked(), true);
        await page.getByRole('button', { name: 'Start', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.signal-scope').classList.contains('capturing'));
        await heading.click({ position: { x: 4, y: 10 } });
        assert.equal(await adapter.isVisible(), true);
        assert.equal(await adapter.isChecked(), true);
        assert.equal(await adapter.isDisabled(), true);
        assert.deepEqual([...sockets.keys()], ['host-a']);
        assert.equal(commands.filter(item => item.type === 'start').length, 1);
        const sidebar = await page.locator('.scope-sidebar').boundingBox();
        const results = await page.locator('.scope-results').boundingBox();
        assert(sidebar.x + sidebar.width <= results.x);
        await page.setViewportSize({ width: 390, height: 844 });
        const narrowSidebar = await page.locator('.scope-sidebar').boundingBox();
        const narrowResults = await page.locator('.scope-results').boundingBox();
        assert(narrowSidebar.y + narrowSidebar.height <= narrowResults.y);
        assert(narrowResults.x + narrowResults.width <= 390);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });

    test(`${name}: multiple hosts, live search, endpoint names, details, bounded rendering and stop`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        page.setDefaultTimeout(10000);
        const { commands, sockets } = await mountAsync(page, false);
        assert.equal(await page.getByText('Capture limits', { exact: true }).count(), 0);
        assert.equal(await page.getByRole('searchbox', { name: 'Advanced Search', exact: true }).count(), 0);
        assert(await page.getByRole('button', { name: 'Start', exact: true }).isDisabled());
        await page.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        await page.getByRole('checkbox', { name: 'host-b', exact: true }).check();
        await page.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true }).nth(0).check();
        await page.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true }).nth(1).check();
        await page.getByRole('button', { name: 'Start', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.scope-status').textContent.includes('Merged: 200'));
        assert((await page.locator('[data-packet]').count()) < 80);
        assert.equal(commands.filter(item => item.type === 'start').length, 2);
        assert(commands.filter(item => item.type === 'start').every(item => item.excludedTcpEndpoints.length === 2));
        assert.equal(commands.filter(item => item.type === 'detail' || item.type === 'search').length, 0);
        const updates = [...sockets.values()].map(socket => new Promise((resolve, reject) =>
        {
            const timeout = setTimeout(() => reject(new Error('The changed proxy endpoint was not excluded.')), 3000);

            /** 双方の取得元へ変更した外側端点が反映されたことを確認します。 */
            const listener = raw =>
            {
                const message = JSON.parse(String(raw));
                if (message.type === 'exclusions' && message.excludedEndpoints?.some(endpoint => endpoint.transport === 'udp' && endpoint.port === 61003))
                {
                    clearTimeout(timeout);
                    socket.off('message', listener);
                    assert.equal(message.excludedTcpEndpoints.length, 2);
                    resolve();
                }
            };

            socket.on('message', listener);
        }));
        sockets.get('host-a').send(frame({ type: 'transport', endpoints: [{ address: '127.0.0.1', port: 61001 }], excludedEndpoints: [{ transport: 'udp', address: '127.0.0.1', port: 61003 }] }));
        await Promise.all(updates);
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('osc AND (192.168.10. OR 192.168.20.) AND "/address/to" AND "ready"');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 1);
        await page.getByRole('combobox', { name: 'Endpoint display' }).selectOption('host');
        await page.waitForFunction(() => document.querySelector('[data-packet]')?.textContent.includes('host-a:9001'));
        assert((await page.locator('[data-packet]').innerText()).includes('host-b:9000'));
        await page.locator('[data-packet]').click();
        await page.waitForFunction(() => document.querySelector('.scope-details pre')?.textContent.includes('OSC packet 42'));
        assert((await page.locator('.scope-details pre').nth(1).innerText()).includes('74 65 73 74'));
        assert.equal(commands.filter(item => item.type === 'detail').length, 1);
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('host-a AND "and" AND "or"');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 1);
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('host-a AND');
        await page.waitForFunction(() => document.querySelector('.scope-search').getAttribute('aria-invalid') === 'true');
        assert.equal(await page.locator('[data-packet]').count(), 1);
        await page.getByRole('button', { name: 'Stop', exact: true }).click();
        await page.waitForFunction(() => !document.querySelector('.signal-scope').classList.contains('capturing'));
        assert.equal(commands.filter(item => item.type === 'stop').length, 2);
        assert((await page.locator('.scope-details pre').first().innerText()).includes('OSC packet 42'));
        await page.getByRole('button', { name: 'Clear', exact: true }).click();
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 0);
        assert.deepEqual(await page.evaluate(() => window.scopeErrors.filter(Boolean)), []);
        assert.equal(commands.filter(item => item.type === 'search').length, 0);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });

    test(`${name}: experimental history limits and source failure remain explicit`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const { sockets } = await mountAsync(page, true);
        await page.getByText('Capture limits', { exact: true }).click();
        assert.equal(await page.getByLabel('Packets', { exact: true }).inputValue(), '50000');
        assert.equal(await page.getByLabel('Memory (MiB)', { exact: true }).inputValue(), '320');
        await page.getByLabel('Packets', { exact: true }).fill('100');
        await page.getByRole('button', { name: 'Apply limits', exact: true }).click();
        await page.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        await page.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true }).check();
        await page.getByRole('button', { name: 'Start', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.scope-status')?.textContent.includes('Rolled out: 100'));
        sockets.get('host-a').send(frame({ type: 'error', message: 'Capture source unavailable' }));
        await page.waitForFunction(() => window.scopeErrors.some(message => message.includes('Capture source unavailable')));
        assert(await page.getByRole('button', { name: 'Apply limits', exact: true }).isEnabled());
        sockets.get('host-a').close();
        await page.waitForFunction(() => !document.querySelector('.signal-scope').classList.contains('capturing'));
        assert(await page.getByRole('button', { name: 'Start', exact: true }).isDisabled());
        assert((await page.locator('.scope-hosts').innerText()).includes('Disconnected'));
        assert((await page.locator('.scope-status').innerText()).includes('Rolled out: 100'));
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });

    test(`${name}: scrolling away holds the viewport, returning to the bottom resumes following new packets`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
        page.setDefaultTimeout(5000);
        const { sockets } = await mountAsync(page, false);
        await page.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        await page.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true }).check();
        await page.getByRole('button', { name: 'Start', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('[data-packet="200"]'));
        const viewport = page.locator('.scope-list');
        await viewport.press('Home');
        await page.waitForFunction(() => document.querySelector('.scope-list').scrollTop < 30);

        /** 実ソケットへ新着を送り、描画だけのスクロール操作と照合します。 */
        function append(index)
        {
            sockets.get('host-a').send(frame({ packetId: index, queryId: 0, matched: true, sourceId: 'pcap:nic', observationPoint: 'rx', fingerprint: 'scroll-' + index,
                time: '2025-01-02T04:00:01.000Z', source: '192.168.10.10', destination: '192.168.20.10', sourcePort: 9001, destinationPort: 9000,
                protocol: 'OSC', transport: 'UDP', summary: 'New packet ' + index }, Buffer.alloc(0)));
        }

        append(201);
        await page.waitForFunction(() => document.querySelector('.scope-status').textContent.includes('/ 201'));
        assert((await viewport.evaluate(element => element.scrollTop)) < 30);
        await viewport.press('End');
        await page.waitForFunction(() => document.querySelector('[data-packet="201"]'));
        append(202);
        await page.waitForFunction(() => document.querySelector('[data-packet="202"]'));
        assert((await viewport.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop)) < 30);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });

    test(`${name}: ordinary search stays local while experimental advanced text and hex query retained source contents`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage();
        const { commands, sockets } = await mountAsync(page, true);
        await page.getByRole('checkbox', { name: 'host-a', exact: true }).check();
        await page.getByRole('checkbox', { name: 'Test NIC (pcap)', exact: true }).check();
        await page.getByRole('button', { name: 'Start', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.scope-status').textContent.includes('/ 200'));
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('deep-only');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 0);
        assert.equal(commands.filter(item => item.type === 'search').length, 0);
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('OSC');
        await page.getByText('Advanced Search', { exact: true }).click();
        await page.getByRole('searchbox', { name: 'Advanced Search', exact: true }).fill('deep-only');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 1);
        assert.equal(commands.filter(item => item.type === 'search').length, 1);
        sockets.get('host-a').send(frame({ packetId: 201, queryId: 0, matched: true, sourceId: 'pcap:nic', observationPoint: 'rx', fingerprint: 'late-row', time: '2025-01-02T04:00:00.000Z',
            source: '192.168.10.10', destination: '192.168.20.10', sourcePort: 9001, destinationPort: 9000, protocol: 'OSC', transport: 'UDP', summary: 'Delivery completed after the search response.' }, Buffer.alloc(0)));
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 2);
        await page.getByRole('combobox', { name: 'Advanced search mode' }).selectOption('hex');
        await page.getByRole('searchbox', { name: 'Advanced Search', exact: true }).fill('74 65 73 74');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 1 && !document.querySelector('.scope-advanced [role=status]').textContent);
        assert(commands.some(item => item.type === 'search' && item.mode === 'hex' && item.expression.term === '74657374'));
        await page.getByRole('searchbox', { name: 'Search captured packets' }).fill('unknown-host');
        await page.waitForFunction(() => document.querySelectorAll('[data-packet]').length === 0);
        await page.evaluate(async () =>
        {
            window.scopeLifetime.abort();
            await window.scope.dispose();
        });
    });
}
