import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { chromium, firefox } from 'playwright';
import { WebSocketServer } from 'ws';

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    test(`${name}: state delivery coalesces slow rendering, reconnects once and stops on disposal`, { timeout: 30000 }, async context =>
    {
        const source = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/wf-dashboard-state.js', import.meta.url));
        const server = createServer((request, response) =>
        {
            response.setHeader('Content-Type', request.url === '/' ? 'text/html' : 'text/javascript');
            response.end(request.url === '/' ? '<!doctype html><p>State observation fixture</p>' : source);
        });
        const webSockets = new WebSocketServer({ server }), sockets = [], commands = [];
        webSockets.on('connection', socket =>
        {
            sockets.push(socket);
            socket.on('message', raw =>
            {
                commands.push(JSON.parse(String(raw)));
                socket.send(JSON.stringify([{ id: 'card', exists: true, text: 'initial', time: '2026-10-13T00:00:00Z' }]));
            });
        });
        await new Promise(resolve =>
        {
            server.listen(0, '127.0.0.1', resolve);
        });
        context.after(async () =>
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
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}/`);
        await page.evaluate(async () =>
        {
            const { watch } = await import('/wf-dashboard-state.js');
            window.received = [];
            window.blocked = true;
            window.view = watch({
                /** 遅い.NET描画の完了を試験から制御します。
                 * @returns 描画の完了です。
                 */
                invokeMethodAsync: async (...args) =>
                {
                    window.received.push(args);
                    if (window.blocked)
                    {
                        await new Promise(resolve =>
                        {
                            window.release = resolve;
                        });
                    }
                }
            }, 7, 'cluster', 'peer', 'instance', [{ id: 'card', target: { workflowId: '', scope: 'cluster', key: 'cue' } }]);
        });
        await page.waitForFunction(() => window.received.length === 1);
        for (let index = 0; index < 100; index++)
        {
            sockets[0].send(JSON.stringify([{ id: 'card', exists: true, text: String(index), time: '2026-10-13T00:00:01Z' }]));
        }
        await page.evaluate(() =>
        {
            window.blocked = false;
            window.release();
        });
        await page.waitForFunction(() => window.received.some(call => call[2].some(value => value.text === '99')));
        assert.equal(commands.length, 1);
        assert.equal(await page.evaluate(() => window.received[0][1]), 7);
        sockets[0].close();
        await page.waitForFunction(() => window.received.some(call => call[3] === 'State observation disconnected'));
        await page.waitForFunction(() => window.received.filter(call => call[2].some(value => value.text === 'initial')).length === 2);
        assert.equal(commands.length, 2);
        assert.deepEqual(commands[0], commands[1]);
        await page.evaluate(() => window.view.dispose());
        assert.equal(sockets.length, 2);
        await page.close();
    });
}
