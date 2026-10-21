import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { setTimeout } from 'node:timers/promises';
import { chromium, firefox } from 'playwright';

const root = path.resolve(process.env.CONTENTS_DESKTOP_WEBROOT ?? fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url)));
const h264Picture = await readFile(new URL('./fixtures/clipboard-frame.h264', import.meta.url));
const vp9Picture = await readFile(new URL('./fixtures/clipboard-frame.vp9', import.meta.url));
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
            assert(file.startsWith(root + path.sep));
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

/** 実WebSocketへ送るホスト側のフレームを構成します。
 * @returns 有限長の通知です。
 */
function frame(kind, value)
{
    const payload = Buffer.isBuffer(value) ? value : Buffer.from(JSON.stringify(value));
    const bytes = Buffer.alloc(24 + payload.length);
    bytes.writeInt32BE(kind, 0);
    bytes.writeInt32BE(payload.length, 8);
    payload.copy(bytes, 24);
    return bytes;
}

/** 受信された操作だけを待ち、固定秒数で成否を決めません。
 * @returns 観測条件の成立です。
 */
async function untilAsync(condition)
{
    const deadline = Date.now() + 10000;
    while (!condition())
    {
        assert(Date.now() < deadline, 'Expected clipboard operation did not arrive.');
        await setTimeout(10);
    }
}

/** 実画面とWebSocketを接続し、OSクリップボードだけを安全なメモリへ置き換えます。
 * @returns 入力の観測先と反映確認を返す操作です。
 */
async function mountAsync(page, experimental, supported = true)
{
    const messages = [], monitors = [{ id: 0, name: 'Clipboard display', left: 0, top: 0, width: 1280, height: 720, primary: true }];
    let remote;
    await page.routeWebSocket('**/client/desktop?*', socket =>
    {
        assert.equal(new URL(socket.url()).searchParams.get('clipboard'), '1');
        remote = socket;
        let details = null, videoTimer = null, timestamp = 0;
        socket.onClose(() => clearInterval(videoTimer));
        socket.onMessage(message =>
        {
            const bytes = Buffer.from(message), values = Array.from({ length: 6 }, (_, index) => bytes.readInt32BE(index * 4));
            messages.push({ kind: values[0], values, text: values[0] === 16 ? bytes.subarray(24).toString('utf8') : null });
            if (values[0] === 9)
            {
                details = { maximumKeyFrameInterval: values[2], maximumPFrames: values[3] === -1 ? null : values[3], maximumBFrames: values[4], quality: values[5] === -1 ? null : values[5] };
            }
            if (values[0] === 6)
            {
                const picture = values[2] === 1 ? vp9Picture : h264Picture;
                socket.send(frame(3, [{ id: 0, encoder: 'fixture', codec: values[2], selection: values[3], framesPerSecond: 60, maximumBitrateKbps: 10240,
                    variableBitrate: true, codecDetails: details, scanCodeInput: true, unicodeInput: true }]));
                socket.send(frame(4, { sampleRate: 48000, channels: 2, format: 'f32le' }));
                clearInterval(videoTimer);

                // Keep an actual synthetic image flowing while clipboard gestures are exercised.
                videoTimer = setInterval(() =>
                {
                    const image = frame(2, picture);
                    image.writeInt32BE(1, 12);
                    image.writeBigInt64BE(BigInt(timestamp += 200000), 16);
                    socket.send(image);
                }, 200);
                videoTimer.unref();
            }
        });
        socket.send(frame(7, { clusterId: 'test', hostId: 'remote', localAudioMuted: false, codecDetails: true, variableBitrate: true, viewOnly: true, clipboardText: supported }));
        socket.send(frame(1, monitors));
    });
    await page.goto(origin + (experimental ? '/?experimental=true' : '/?experimental=false'));
    await page.evaluate(async () =>
    {
        window.clipboardValue = 'initial client';
        window.clipboardReads = 0;
        window.notices = [];
        Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {

            readText: async () =>
            {
                window.clipboardReads++;
                return window.clipboardValue;
            },

            writeText: async text =>
            {
                window.clipboardValue = text;
            }
        } });
        const { desktopView } = await import('/remote-desktop.js');
        const ctx = { signal: new AbortController().signal,

            t: en => en,

            say: value => window.notices.push(value),

            icon: () => document.createElement('span'),

            peers: async () => [{ hostId: 'remote', clusterId: 'test' }],

            /** 製品のボタン操作を実DOMへ接続します。
             * @returns 押下可能な要素です。
             */
            button(label, action)
            {
                const button = document.createElement('button');
                button.textContent = label;
                button.onclick = action;
                return button;
            } };
        window.desktop = await desktopView(document.querySelector('#desktop'), ctx);
    });
    await page.getByLabel('Host', { exact: true }).selectOption('remote');
    await page.getByLabel('Transmission preset', { exact: true }).selectOption('lan');
    const codec = page.getByLabel('Video codec', { exact: true });
    assert.equal(await codec.inputValue(), 'vp9');
    assert.deepEqual(await codec.locator('option').allTextContents(), experimental ? ['VP9', 'H.264', 'H.265'] : ['VP9']);
    if (experimental)
    {
        await page.locator('.desktop-menu').filter({ has: page.getByText('Connection settings', { exact: true }).first() }).locator('summary').click();
        await codec.selectOption('h264');
    }
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.desktop-connection')?.textContent === 'Connected');
    await page.waitForFunction(() => 0 < parseFloat(document.querySelector('.desktop-stats').children[1].lastElementChild.textContent));

    /** 本文と操作IDを混同しないホスト通知を送ります。 */
    function notify(mode, id, sequence, text = '')
    {
        const body = Buffer.from(text), payload = Buffer.alloc(12 + body.length);
        payload.writeInt32BE(mode, 0);
        payload.writeInt32BE(id, 4);
        payload.writeUInt32BE(sequence, 8);
        body.copy(payload, 12);
        remote.send(frame(15, payload));
    }

    return { messages, notify };
}

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    test(`${name}: native focus loss before blur neither starts clipboard reads nor shows late warnings`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const { messages, notify } = await mountAsync(page, true);
        await page.locator('.desktop-viewport').click({ position: { x: 100, y: 100 } });
        await untilAsync(() => messages.some(message => message.kind === 16));
        notify(1, messages.findLast(message => message.kind === 16).values[1], 1);
        await page.evaluate(() =>
        {
            window.clipboardFocused = false;
            window.clipboardReads = 0;
            window.notices.length = 0;
            Object.defineProperty(document, 'hasFocus', { configurable: true,

                value: () => window.clipboardFocused });

            navigator.clipboard.readText = async () =>
            {
                window.clipboardReads++;
                throw new Error('Clipboard access denied');
            };
            const viewport = document.querySelector('.desktop-viewport');
            viewport.dispatchEvent(new Event('pointerleave'));
            viewport.dispatchEvent(new FocusEvent('focusin'));
            window.dispatchEvent(new Event('focus'));
        });
        notify(0, 0, 2, 'inactive host text');
        await page.evaluate(() => new Promise(resolve =>
        {
            requestAnimationFrame(resolve);
        }));
        assert.deepEqual(await page.evaluate(() => ({ reads: window.clipboardReads, notices: window.notices })), { reads: 0, notices: [] });
        await page.evaluate(() =>
        {
            window.clipboardFocused = true;
            document.querySelector('.desktop-viewport').dispatchEvent(new FocusEvent('focusin'));
        });
        await page.waitForFunction(() => window.notices.some(text => text.startsWith('Clipboard access was not allowed.')));
        await page.evaluate(() => window.desktop.dispose());
    });

    // LEGACY-SPEC: ページの既定OFFと手動購読開始は、既定ON・操作領域への入場時同期へ変更しました。
    test(`${name}: normal clipboard button, client priority and ordered paste`, { timeout: 30000, skip: 'Legacy page default OFF; replaced by default ON lifecycle coverage.' }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        page.setDefaultTimeout(10000);
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const { messages, notify } = await mountAsync(page, false);
        const button = page.getByRole('button', { name: 'Clipboard synchronization', exact: true });
        assert.equal(await button.getAttribute('aria-pressed'), 'false');
        assert.equal(await page.getByRole('button', { name: 'Cursor synchronization', exact: true }).evaluate(element => element.nextElementSibling.getAttribute('aria-label')), 'Clipboard synchronization');
        assert.equal(messages.some(message => message.kind === 15 || message.kind === 16), false);
        await button.click();
        await untilAsync(() => messages.some(message => message.kind === 16));
        const initial = messages.findLast(message => message.kind === 16);
        assert.equal(initial.text, 'initial client');
        notify(1, initial.values[1], 1);
        await page.locator('.desktop-viewport').click({ position: { x: 100, y: 100 } });
        notify(0, 0, 2, 'host copy 日本語');
        await page.waitForFunction(() => window.clipboardValue === 'host copy 日本語');
        await page.evaluate(() =>
        {
            window.dispatchEvent(new Event('blur'));
            window.clipboardValue = 'fresh client';
        });
        notify(0, 0, 3, 'inactive host copy');
        await page.evaluate(() => window.dispatchEvent(new Event('focus')));
        try
        {
            await untilAsync(() => messages.some(message => message.kind === 16 && message.text === 'fresh client'));
        }
        catch (error)
        {
            context.diagnostic(JSON.stringify({ messages: messages.slice(-25), state: await page.evaluate(() => ({ reads: window.clipboardReads, value: window.clipboardValue,
                notices: window.notices, active: document.activeElement?.className, hover: document.querySelector('.desktop-viewport').matches(':hover') })) }));
            throw error;
        }
        const returning = messages.findLast(message => message.kind === 16);
        assert.equal(returning.text, 'fresh client');
        notify(1, returning.values[1], 4);
        notify(0, 0, 3, 'stale host copy');
        await page.waitForFunction(() => window.clipboardValue === 'fresh client');
        const start = messages.length;
        await page.locator('.desktop-viewport').dispatchEvent('keydown', { key: 'v', code: 'KeyV', ctrlKey: true });
        await page.locator('.desktop-viewport').evaluate(element =>
        {
            const data = new DataTransfer();
            data.setData('text/plain', 'paste event value');
            const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });

            // Firefoxは合成ClipboardEventの初期値を採用しないため、OSを読まない境界を明示します。
            Object.defineProperty(event, 'clipboardData', { value: data });
            window.clipboardPasteData = { source: data.getData('text/plain'), actual: event.clipboardData?.getData('text/plain'), types: [...(event.clipboardData?.types ?? [])] };
            element.dispatchEvent(event);
        });
        try
        {
            await untilAsync(() => messages.slice(start).some(message => message.kind === 16));
        }
        catch (error)
        {
            context.diagnostic(JSON.stringify({ messages: messages.slice(start), state: await page.evaluate(() => ({ paste: window.clipboardPasteData, notices: window.notices })) }));
            throw error;
        }
        await page.locator('.desktop-viewport').dispatchEvent('keydown', { key: 'a', code: 'KeyA' });
        assert.equal(messages.slice(start).some(message => message.kind === 4 && message.values[4] === 86), false);
        const pasting = messages.findLast(message => message.kind === 16);
        assert.equal(pasting.text, 'paste event value');
        notify(1, pasting.values[1], 5);
        await untilAsync(() => messages.slice(start).some(message => message.kind === 4 && message.values[4] === 65 && message.values[5] === 1));
        const keys = messages.slice(start).filter(message => message.kind === 4 && message.values[5] === 1).map(message => message.values[4]);
        assert((0) <= (keys.indexOf(86)) && keys.indexOf(86) < keys.indexOf(65));
        await page.getByRole('button', { name: 'View-only mode', exact: true }).click();
        assert(await button.isDisabled());
        await page.evaluate(() => window.desktop.dispose());
        assert.deepEqual(errors, []);
    });

    test(`${name}: non-text and unchanged text paste the host clipboard without sending stale text`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const { messages, notify } = await mountAsync(page, false);
        await page.locator('.desktop-viewport').click({ position: { x: 100, y: 100 } });
        await untilAsync(() => messages.some(message => message.kind === 16));
        notify(1, messages.findLast(message => message.kind === 16).values[1], 1);
        await page.locator('.desktop-viewport').click({ position: { x: 100, y: 100 } });
        for (const value of ['initial client', null])
        {
            const start = messages.length;
            await page.locator('.desktop-viewport').evaluate((element, text) =>
            {
                const data = new DataTransfer();
                if (text === null)
                {
                    data.items.add(new File(['image'], 'image.png', { type: 'image/png' }));
                }
                else
                {
                    data.setData('text/plain', text);
                }
                const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
                Object.defineProperty(event, 'clipboardData', { value: data });
                element.dispatchEvent(event);
            }, value);
            await untilAsync(() => messages.slice(start).some(message => message.kind === 4 && message.values[4] === 86 && message.values[5] === 1));
            assert.equal(messages.slice(start).some(message => message.kind === 16), false);
        }
        await page.evaluate(() =>
        {
            window.dispatchEvent(new Event('blur'));
            window.clipboardValue = 'new client after image';
            window.dispatchEvent(new Event('focus'));
        });
        await untilAsync(() => messages.some(message => message.kind === 16 && message.text === 'new client after image'));
        notify(1, messages.findLast(message => message.kind === 16).values[1], 2);
        assert.deepEqual(await page.evaluate(() => window.notices.filter(Boolean)), []);
        await page.evaluate(() => window.desktop.dispose());
    });

    test(`${name}: clipboard defaults ON, synchronizes on entry and preserves a manual OFF across reconnects`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const { messages, notify } = await mountAsync(page, false);
        const button = page.getByRole('button', { name: 'Clipboard synchronization', exact: true });
        assert.equal(await button.getAttribute('aria-pressed'), 'true');
        assert(messages.some(message => message.kind === 15 && message.values[1] === 1));
        assert.equal(messages.some(message => message.kind === 16), false);
        await page.locator('.desktop-viewport').click({ position: { x: 100, y: 100 } });
        await untilAsync(() => messages.some(message => message.kind === 16));
        const initial = messages.findLast(message => message.kind === 16);
        assert.equal(initial.text, 'initial client');
        notify(1, initial.values[1], 1);
        notify(0, 0, 2, 'host copy');
        await page.waitForFunction(() => window.clipboardValue === 'host copy');
        await page.getByRole('button', { name: 'View-only mode', exact: true }).click();
        assert(await button.isDisabled());
        await page.getByRole('button', { name: 'View-only mode', exact: true }).click();
        await button.click();
        assert.equal(await button.getAttribute('aria-pressed'), 'false');
        await page.getByRole('button', { name: 'Disconnect', exact: true }).click();
        const beforeReconnect = messages.length;
        await page.getByRole('button', { name: 'Connect', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.desktop-connection')?.textContent === 'Connected');
        assert.equal(await button.getAttribute('aria-pressed'), 'false');
        assert.equal(messages.slice(beforeReconnect).some(message => message.kind === 16 || message.kind === 15 && message.values[1] === 1), false);
        await page.evaluate(() => window.desktop.dispose());
        const reopened = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        await mountAsync(reopened, false);
        assert.equal(await reopened.getByRole('button', { name: 'Clipboard synchronization', exact: true }).getAttribute('aria-pressed'), 'true');
        await reopened.evaluate(() => window.desktop.dispose());
    });

    test(`${name}: normal and experimental pages disable clipboard controls for older hosts`, { timeout: 30000 }, async context =>
    {
        const browser = await engine.launch({ headless: true, ...options });
        context.after(() => browser.close());
        for (const experimental of [false, true])
        {
            const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
            const { messages } = await mountAsync(page, experimental, false);
            const button = page.getByRole('button', { name: 'Clipboard synchronization', exact: true });
            assert.equal(await button.count(), 1);
            assert(await button.isDisabled());
            assert.equal(messages.some(message => message.kind === 15 || message.kind === 16), false);
            await page.evaluate(() => window.desktop.dispose());
            await page.close();
        }
    });
}
