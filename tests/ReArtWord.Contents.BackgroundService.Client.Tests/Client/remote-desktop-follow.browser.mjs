import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { setTimeout } from 'node:timers/promises';
import { chromium, firefox } from 'playwright';

const root = path.resolve(process.env.CONTENTS_DESKTOP_WEBROOT ?? fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url)));
const picture = await readFile(new URL('./fixtures/clipboard-frame.vp9', import.meta.url));
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
            assert.ok(file.startsWith(root + path.sep));
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

/** 製品と同じ形式で、試験用の接続先から応答します。
 * @returns JSON通知のパケットです。
 */
function frame(kind, value)
{
    const payload = Buffer.isBuffer(value) ? value : Buffer.from(JSON.stringify(value)), result = Buffer.alloc(24 + payload.length);
    result.writeInt32BE(kind, 0);
    result.writeInt32BE(payload.length, 8);
    payload.copy(result, 24);
    return result;
}

/** 実際の操作と共有ポインターを受信し、OS入力へは接続しません。
 * @returns 送信されたパケットの一覧です。
 */
async function mountAsync(page, annotations)
{
    const sent = [], monitors = [
        { id: 0, name: 'Display 1 - primary', left: 0, top: 0, width: 2560, height: 1920, primary: true },
        { id: 1, name: 'Display 2 - secondary', left: -1280, top: 0, width: 1280, height: 1024, primary: false }
    ];
    await page.routeWebSocket('**/client/desktop?*', socket =>
    {
        let details = null, videoTimer = null, timestamp = 0;
        socket.onClose(() => clearInterval(videoTimer));
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
                const selected = values[1] === -1 ? monitors : monitors.filter(monitor => monitor.id === values[1]);
                socket.send(frame(3, selected.map(monitor => ({ id: monitor.id, encoder: 'fixture', codec: values[2], selection: values[3], framesPerSecond: 60, maximumBitrateKbps: 10240,
                    variableBitrate: true, codecDetails: details, scanCodeInput: true, unicodeInput: true }))));
                socket.send(frame(4, { sampleRate: 48000, channels: 2, format: 'f32le' }));

                // 表示配置の確認中も、選択中の画面へ試験用の映像を送ります。
                clearInterval(videoTimer);
                videoTimer = setInterval(() =>
                {
                    timestamp += 200000;
                    for (const monitor of selected)
                    {
                        const image = frame(2, picture);
                        image.writeInt32BE(monitor.id, 4);
                        image.writeInt32BE(1, 12);
                        image.writeBigInt64BE(BigInt(timestamp), 16);
                        socket.send(image);
                    }
                }, 200);
                videoTimer.unref();
            }
        });
        socket.send(frame(7, { clusterId: 'test', hostId: 'remote', localAudioMuted: false, codecDetails: true, variableBitrate: true, viewOnly: annotations, annotations }));
        socket.send(frame(1, monitors));
    });
    await page.goto(origin);
    await page.evaluate(async () =>
    {
        const { desktopView } = await import('/remote-desktop.js');
        const ctx = { signal: new AbortController().signal,

            t: en => en,

            say: text => window.notices.push(text),

            icon: () => document.createElement('span'),

            peers: async () => [{ hostId: 'remote', clusterId: 'test' }],

            /** ブラウザーで実際に押せる操作を構成します。
             * @returns 製品の処理へ接続されたボタンです。
             */
            button(label, action)
            {
                const button = document.createElement('button');
                button.textContent = label;
                button.onclick = action;
                return button;
            } };
        window.notices = [];
        window.desktop = await desktopView(document.querySelector('#desktop'), ctx);
    });
    await page.getByLabel('Host', { exact: true }).selectOption('remote');
    await page.getByLabel('Transmission preset', { exact: true }).selectOption('lan');
    await page.getByRole('button', { name: 'View-only mode', exact: true }).click();
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.desktop-connection')?.textContent === 'Connected');
    await page.getByRole('button', { name: 'Dot by dot (1:1)', exact: true }).click();
    const grouped = await page.getByRole('group', { name: 'Displays', exact: true }).evaluate(displays => ({
        separator: displays.nextElementSibling?.className,
        next: displays.nextElementSibling?.nextElementSibling?.getAttribute('aria-label'),
        enabled: displays.nextElementSibling?.nextElementSibling?.getAttribute('aria-pressed'),
        gap: parseFloat(getComputedStyle(displays.parentElement).columnGap) + parseFloat(getComputedStyle(displays.nextElementSibling).marginRight),
        spacing: displays.nextElementSibling?.nextElementSibling?.getBoundingClientRect().left - displays.nextElementSibling?.getBoundingClientRect().right
    }));
    assert.equal(grouped.separator, 'desktop-key-separator');
    assert.equal(grouped.next, 'Automatic reconnect');
    assert.equal(grouped.enabled, 'true');
    assert.ok(Math.abs(grouped.spacing - grouped.gap) <= 1);
    return sent;
}

/** スクロール後の映像と番号表示の位置を実レイアウトから取得します。
 * @returns 表示座標とスクロール範囲です。
 */
async function geometryAsync(page)
{
    return page.locator('.desktop-viewport').evaluate(viewport =>
    {
        const bounds = viewport.getBoundingClientRect(), panel = viewport.querySelector('.desktop-monitor');
        const header = panel.querySelector(':scope > span'), label = header.firstElementChild ?? header;
        return { left: bounds.left + viewport.clientLeft, top: bounds.top + viewport.clientTop, width: viewport.clientWidth, height: viewport.clientHeight,
            scrollLeft: viewport.scrollLeft, scrollTop: viewport.scrollTop, horizontal: viewport.scrollWidth - viewport.clientWidth, vertical: viewport.scrollHeight - viewport.clientHeight,
            headerTop: header.getBoundingClientRect().top, labelLeft: label.getBoundingClientRect().left, canvas: panel.querySelector('canvas').getBoundingClientRect().toJSON() };
    });
}

/** 入力後の描画を待ってから、スクロールしない操作の結果を確認します。
 * @returns 次の描画までの待機です。
 */
async function renderedAsync(page)
{
    await page.evaluate(() => new Promise(resolve =>
    {
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    }));
}

/** 全画面への切替後の表示幅を使い、追従先に到着するまで待ちます。
 * @returns 指定座標への追従完了です。
 */
async function followedAsync(page, x)
{
    await page.waitForFunction(x =>
    {
        const viewport = document.querySelector('.desktop-viewport'), bounds = viewport.getBoundingClientRect();
        const range = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
        const wanted = range * (x - bounds.left - viewport.clientLeft) / (viewport.clientWidth - 1);
        return Math.abs(viewport.scrollLeft - wanted) <= 2;
    }, x);
}

for (const [name, engine, options] of [['Chrome', chromium, { channel: 'chrome' }], ['Firefox', firefox, {}]])
{
    for (const [density, annotations] of [[1, true], [2, false]])
    {
        test(`${name}: view-only follow, pinned headers and density ${density}`, { timeout: 30000 }, async context =>
        {
            const browser = await engine.launch({ headless: true, ...options });
            context.after(() => browser.close());
            const page = await browser.newPage({ viewport: { width: 1024, height: 800 }, deviceScaleFactor: density });
            page.setDefaultTimeout(10000);
            const errors = [];
            page.on('pageerror', error => errors.push(error.message));
            const sent = await mountAsync(page, annotations), start = sent.length;
            const original = await geometryAsync(page);
            assert.ok(0 < original.horizontal && 0 < original.vertical);
            assert.ok(Math.abs(original.headerTop - original.top) <= 1, 'no gap above the display header');
            for (const fraction of [0.25, 0.75])
            {
                const x = original.left + original.width * fraction, y = original.top + original.height * 0.7;
                await page.mouse.move(x, y);
                const wanted = original.horizontal * (x - original.left) / (original.width - 1);
                await page.waitForFunction(wanted => Math.abs(document.querySelector('.desktop-viewport').scrollLeft - wanted) <= 2, wanted);
                const current = await geometryAsync(page);
                assert.ok(0 < current.scrollTop);
                assert.ok(Math.abs(current.headerTop - original.top) <= 1);
                assert.ok(Math.abs(current.labelLeft - original.labelLeft) <= 1, 'display number stays fixed horizontally');
                if (annotations)
                {
                    const expected = [13, 0, Math.floor((x - current.canvas.left) * 2560 / current.canvas.width), Math.floor((y - current.canvas.top) * 1920 / current.canvas.height)];
                    while (sent.findLast(packet => packet[0] === 13)?.[2] !== expected[2])
                    {
                        await setTimeout(10, undefined, { signal: context.signal });
                    }
                    assert.deepEqual(sent.findLast(packet => packet[0] === 13).slice(0, 4), expected);
                }
            }
            await page.getByRole('button', { name: 'Follow pointer', exact: true }).click();
            const stopped = await geometryAsync(page);
            await page.mouse.move(original.left + 30, original.top + 80);
            await renderedAsync(page);
            assert.equal((await geometryAsync(page)).scrollLeft, stopped.scrollLeft);
            await page.getByRole('button', { name: 'Follow pointer', exact: true }).click();
            await page.getByRole('button', { name: 'Fullscreen', exact: true }).click();
            await page.waitForFunction(() => document.fullscreenElement?.id === 'desktop');
            const fullscreen = await geometryAsync(page);
            await page.mouse.move(fullscreen.left + fullscreen.width * 0.5, fullscreen.top + fullscreen.height * 0.5);
            await followedAsync(page, fullscreen.left + fullscreen.width * 0.5);
            const pinned = await geometryAsync(page);
            assert.ok(Math.abs(pinned.headerTop - pinned.top) <= 1);
            assert.ok(Math.abs(pinned.labelLeft - pinned.left - 8) <= 1);
            await page.getByRole('button', { name: 'All screens side by side', exact: true }).press('Enter');
            try
            {
                await page.waitForFunction(() => document.querySelectorAll('.desktop-monitor').length === 2);
            }
            catch (error)
            {
                context.diagnostic(JSON.stringify({ page: await page.evaluate(() => ({ notices: window.notices, monitors: document.querySelectorAll('.desktop-monitor').length,
                    connection: document.querySelector('.desktop-connection')?.textContent, selected: document.querySelector('.desktop-screen-tabs [aria-pressed="true"]')?.getAttribute('aria-label') })),
                selections: sent.filter(packet => packet[0] === 6) }));
                throw error;
            }
            const multiple = await geometryAsync(page);
            await page.mouse.move(multiple.left + multiple.width * 0.6, multiple.top + multiple.height * 0.4);
            await followedAsync(page, multiple.left + multiple.width * 0.6);
            const followed = await geometryAsync(page);
            assert.ok(0 < followed.scrollLeft);
            assert.ok(Math.abs(followed.headerTop - followed.top) <= 1);
            assert.ok(Math.abs(followed.labelLeft - followed.left - 8) <= 1);
            await page.getByRole('button', { name: 'Dot by dot (1:1)', exact: true }).click();
            await page.waitForFunction(() =>
            {
                const viewport = document.querySelector('.desktop-viewport');
                return viewport.scrollWidth <= viewport.clientWidth && viewport.scrollLeft === 0;
            });
            await page.mouse.move(multiple.left + 40, multiple.top + 80);
            await renderedAsync(page);
            assert.equal((await geometryAsync(page)).scrollLeft, 0);
            assert.equal(sent.slice(start).some(packet => [1, 2, 3, 4, 5, 10].includes(packet[0])), false);
            await page.evaluate(() => window.desktop.dispose());
            assert.deepEqual(errors, []);
        });
    }
}
