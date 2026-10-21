import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { setTimeout } from 'node:timers/promises';
import { chromium, firefox } from 'playwright';

/** 実際のRemoteDesktopモジュールを、実機へ入力を送らない接続先で確認します。 */
const root = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url));
let server, origin;
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
                response.end('<!doctype html><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/remote-desktop.css"><main id="desktop"></main>');
                return;
            }
            const file = path.resolve(root, '.' + decodeURIComponent(pathname));
            assert.ok(file.startsWith(root));
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

after(async () =>
{
    await new Promise(resolve =>
    {
        server?.close(resolve);
    });
});

/** WebSocketへ接続情報またはエンコーダーの応答を返します。
 * @returns 製品と同じヘッダーを持つJSON通知です。
 */
function frame(kind, value)
{
    const payload = Buffer.from(JSON.stringify(value)), result = Buffer.alloc(24 + payload.length);
    result.writeInt32BE(kind, 0);
    result.writeInt32BE(payload.length, 8);
    payload.copy(result, 24);
    return result;
}

/** ブラウザーの入力が非同期にWebSocketへ届くまで有限時間だけ待ちます。
 * @returns 指定件数までに届いた操作入力です。
 */
async function inputsAsync(sent, count)
{
    for (let attempt = 0; attempt < 100 && sent.filter(item => item[0] === 4).length < count; attempt++)
    {
        await setTimeout(10);
    }
    return sent.filter(item => item[0] === 4);
}

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    test(`${name}: 接続前の全画面と長押し入力を保持し、映像だけの全画面は切断で解除する`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage();
        const sent = [], errors = [], connections = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.routeWebSocket('**/client/desktop?*', socket =>
        {
            connections.push(socket);
            let details = null;
            socket.onMessage(message =>
            {
                const bytes = Buffer.from(message), values = Array.from({ length: 6 }, (_, index) => bytes.readInt32BE(index * 4));
                sent.push(values);
                if (values[0] === 9)
                {
                    details = { maximumKeyFrameInterval: values[2], maximumPFrames: values[3] === -1 ? null : values[3], maximumBFrames: values[4], quality: values[5] === -1 ? null : values[5] };
                }
                if (values[0] === 6)
                {
                    socket.send(frame(3, [{ id: 0, encoder: 'fixture', codec: values[2], selection: values[3], framesPerSecond: 60, maximumBitrateKbps: 10240,
                        variableBitrate: true, codecDetails: details, scanCodeInput: true, unicodeInput: true }]));
                    socket.send(frame(4, { sampleRate: 48000, channels: 2, format: 'f32le' }));
                }
            });
            socket.send(frame(7, { clusterId: 'test', hostId: 'remote', localAudioMuted: false, codecDetails: true, variableBitrate: true }));
            socket.send(frame(1, [{ id: 0, name: 'Test display', left: 0, top: 0, width: 800, height: 1200, primary: true }]));
        });
        await page.goto(origin);
        await page.evaluate(async () =>
        {
            const { desktopView } = await import('/remote-desktop.js');
            const controller = new AbortController();
            window.notices = [];
            window.keyEvents = [];
            window.lastAction = '';
            const ctx = { signal: controller.signal,

                t: en => en,

                say: value => window.notices.push(value),

                icon: () => document.createElement('span'),

                peers: async () => [{ hostId: 'remote', clusterId: 'test' }],

                /** 操作の非同期完了と失敗をブラウザーテストから観測します。
                 * @returns 実際にクリックできるボタンです。
                 */
                button(label, action)
                {
                    const button = document.createElement('button');
                    button.textContent = label;
                    button.onclick = async () =>
                    {
                        try
                        {
                            await action();
                            window.lastAction = label;
                        }
                        catch (error)
                        {
                            window.notices.push(error.message);
                        }
                    };
                    return button;
                } };
            window.desktop = await desktopView(document.querySelector('#desktop'), ctx);
            document.querySelector('.desktop-viewport').addEventListener('keydown', event => window.keyEvents.push({ code: event.code, repeat: event.repeat }));
        });
        assert.equal(await page.getByRole('button', { name: 'Video fullscreen', exact: true }).isDisabled(), true);
        assert.equal(await page.getByLabel('Video codec', { exact: true }).inputValue(), 'vp9');
        assert.deepEqual(await page.getByLabel('Video codec', { exact: true }).locator('option').allTextContents(), ['VP9']);
        await page.getByRole('button', { name: 'Fullscreen', exact: true }).click();
        await page.waitForFunction(() => window.lastAction === 'Fullscreen');
        assert.equal(await page.evaluate(() => document.fullscreenElement?.id), 'desktop');
        assert.equal(sent.length, 0);
        await page.getByLabel('Host', { exact: true }).selectOption('remote');
        await page.getByLabel('Transmission preset', { exact: true }).selectOption('lan');
        await page.getByRole('button', { name: 'Connect', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('button[aria-label="Show software keyboard"]')?.disabled === false);
        assert.equal(sent.findLast(input => input[0] === 6)[2], 1);
        assert.equal(await page.evaluate(() => document.fullscreenElement?.id), 'desktop');
        assert.equal(await page.getByRole('button', { name: 'Mute audio', exact: true }).getAttribute('data-muted'), 'false');
        await page.getByRole('button', { name: 'Dot by dot (1:1)', exact: true }).click();
        const beforeScroll = await page.locator('.desktop-viewport').evaluate(viewport => ({
            nameTop: viewport.querySelector('.desktop-monitor > span').getBoundingClientRect().top,
            canvasTop: viewport.querySelector('canvas').getBoundingClientRect().top,
        }));
        await page.locator('.desktop-viewport').evaluate(viewport =>
        {
            viewport.scrollTop = 300;
        });
        await page.waitForFunction(() => document.querySelector('.desktop-viewport').scrollTop === 300);
        const afterScroll = await page.locator('.desktop-viewport').evaluate(viewport => ({
            nameTop: viewport.querySelector('.desktop-monitor > span').getBoundingClientRect().top,
            canvasTop: viewport.querySelector('canvas').getBoundingClientRect().top,
        }));
        assert.ok(Math.abs(afterScroll.nameTop - beforeScroll.nameTop) <= 2);
        assert.equal(beforeScroll.canvasTop - afterScroll.canvasTop, 300);
        await page.getByRole('button', { name: 'Dot by dot (1:1)', exact: true }).click();
        await page.locator('.desktop-viewport').focus();
        let total = 0;
        for (const [key, code] of [['a', 65], ['ArrowLeft', 37], ['Backspace', 8], ['Minus', 0x10c]])
        {
            for (let repeat = 0; repeat < 5; repeat++)
            {
                await page.keyboard.down(key);
            }
            await page.keyboard.up(key);
            const keys = await inputsAsync(sent, total + 6);
            assert.deepEqual(keys.slice(total).map(value => value.slice(4)), [...Array.from({ length: 5 }, () => [code, 1]), [code, 0]]);
            total += 6;
        }
        assert.deepEqual(await page.evaluate(() => window.keyEvents.map(event => event.repeat)), Array.from({ length: 4 }, () => [false, true, true, true, true]).flat());
        await page.getByRole('button', { name: 'Show software keyboard', exact: true }).click();
        await page.keyboard.down('Control');
        for (let repeat = 0; repeat < 4; repeat++)
        {
            await page.keyboard.down('ArrowLeft');
        }
        await page.keyboard.up('ArrowLeft');
        await page.keyboard.up('Control');
        const modified = await inputsAsync(sent, total + 7);
        assert.deepEqual(modified.slice(total).map(value => value.slice(4)), [[0xa2, 1], [37, 1], [37, 1], [37, 1], [37, 1], [37, 0], [0xa2, 0]]);
        await page.getByRole('button', { name: 'Hide software keyboard', exact: true }).click();
        assert.equal(await page.getByRole('button', { name: 'Automatic reconnect', exact: true }).getAttribute('aria-pressed'), 'true');
        for (const count of [2, 3])
        {
            const beforeReconnect = sent.length;
            await connections.at(-1).close({ code: 1011, reason: 'Desktop.UpstreamDisconnected' });
            for (let attempt = 0; attempt < 100 && connections.length < count; attempt++)
            {
                await setTimeout(10);
            }
            assert.equal(connections.length, count);
            await page.waitForFunction(() => document.querySelector('.desktop-connection')?.textContent === 'Connected');
            assert.equal(sent.slice(beforeReconnect).some(input => [1, 2, 3, 4, 10].includes(input[0])), false);
        }
        await page.getByRole('button', { name: 'Disconnect', exact: true }).click();
        await page.waitForFunction(() => window.lastAction === 'Disconnect');
        await setTimeout(30);
        assert.equal(connections.length, 3);
        assert.equal(await page.evaluate(() => document.fullscreenElement?.id), 'desktop');
        await page.getByRole('button', { name: 'Exit fullscreen', exact: true }).click();
        await page.waitForFunction(() => document.fullscreenElement === null);
        await page.getByRole('button', { name: 'Connect', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('button[aria-label="Show software keyboard"]')?.disabled === false);
        await page.getByRole('button', { name: 'Video fullscreen', exact: true }).click();
        await page.waitForFunction(() => document.fullscreenElement?.className === 'desktop-viewport');
        await page.evaluate(() => window.desktop.dispose());
        await page.waitForFunction(() => document.fullscreenElement === null);
        assert.deepEqual(errors, []);
    });
}
