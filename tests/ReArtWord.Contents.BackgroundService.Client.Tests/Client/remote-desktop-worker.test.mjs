import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopVideoWorker, supportsDesktopVideoWorker } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-video-worker.js';
import { createDesktopRenderer } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-renderer.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t)
{
    const saved = new Map(['Worker', 'navigator', 'performance', 'setTimeout', 'clearTimeout'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    const workers = [], timers = [], fallbacks = [], refreshes = [], codecs = [];
    let now = 0, active = true;

    /** Workerの通知と終了を試験用に再現します。 */
    class Worker
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(url, options)
        {
            this.url = url;
            this.options = options;
            this.messages = [];
            this.terminated = 0;
            workers.push(this);
        }

        /** 試験対象へのメッセージを記録または配送します。 */
        postMessage(message, transfer)
        {
            this.messages.push({ message, transfer });
        }

        /** 試験用Workerの終了を記録します。 */
        terminate()
        {
            this.terminated++;
        }

        /** 試験で指定した通知を受信側へ渡します。 */
        emit(data)
        {
            this.onmessage({ data });
        }
    }

    Object.defineProperties(globalThis, { Worker: { value: Worker, configurable: true }, navigator: { value: { userAgent: 'Firefox/142' }, configurable: true }, performance: { value: { timeOrigin: 10000,

        now: () => now }, configurable: true },
    setTimeout: { value: callback =>
    {
        timers.push(callback);
        return timers.length;
    },

    configurable: true }, clearTimeout: { value: id =>
    {
        timers[id - 1] = null;
    },

    configurable: true } });
    const surface = {}, canvas = { transferControlToOffscreen: () => surface };
    const proxy = createDesktopVideoWorker({ id: 0, width: 1920, height: 1080 }, canvas, { acceleration: 'prefer-hardware', frameRate: 30, maximumBitrateKbps: 6000,

        active: () => active,

        audible: () => 123456,

        bufferBudget: () => 330,

        backendFallback: reason => fallbacks.push(reason),

        refresh: reason => refreshes.push(reason),

        fallback: reason => codecs.push(reason) });
    t.after(() =>
    {
        proxy.close();
        for (const [name, descriptor] of saved)
        {
            if (descriptor)
            {
                Object.defineProperty(globalThis, name, descriptor);
            }
            else
            {
                delete globalThis[name];
            }
        }
    });
    return { proxy, worker: workers[0], canvas, surface, fallbacks, refreshes, codecs, timers,

        advance: ms =>
        {
            now += ms;
        },

        inactive: () =>
        {
            active = false;
        } };
}

test('Firefox transfers the canvas and original compressed buffer; Chromium keeps its inline path', t =>
{
    const f = fixture(t);
    assert.equal(supportsDesktopVideoWorker(f.canvas), true);
    navigator.userAgent = 'Chrome/140';
    assert.equal(supportsDesktopVideoWorker(f.canvas), false);
    assert.equal(supportsDesktopVideoWorker(f.canvas, 'worker'), true);
    assert.equal(supportsDesktopVideoWorker(f.canvas, 'inline'), false);
    assert.equal(supportsDesktopVideoWorker({}, 'worker'), false);
    assert.equal(f.worker.messages[0].transfer[0], f.surface);
    assert.equal(f.worker.options.type, 'module');
    f.proxy.setCodec('h264');
    const bytes = new Uint8Array(new ArrayBuffer(100), 24);
    f.proxy.receive(bytes, true, 9000);
    const posted = f.worker.messages.at(-1);
    assert.equal(posted.message.payload, bytes);
    assert.equal(posted.transfer[0], bytes.buffer);
    assert.equal(posted.message.arrived, 10000);
    assert.equal(f.proxy.draw(), false);
    assert.equal(f.proxy.hasPending, false);
});

test('worker audio clock messages are bounded and retain the common time origin and buffer budget', t =>
{
    const f = fixture(t);
    for (let i = 0;i < 100;i++)
    {
        f.proxy.tick();
    }
    let clocks = f.worker.messages.filter(item => item.message.type === 'clock');
    assert.equal(clocks.length, 1);
    assert.equal(clocks[0].message.value, 123456);
    assert.equal(clocks[0].message.at, 10000);
    assert.equal(clocks[0].message.budget, 330);
    f.worker.emit({ type: 'clock-accepted' });
    f.advance(25);
    f.proxy.tick();
    clocks = f.worker.messages.filter(item => item.message.type === 'clock');
    assert.equal(clocks.length, 2);
    assert.equal(clocks[1].message.at, 10025);
});

test('worker backlog is bounded by bytes and falls back once', t =>
{
    const f = fixture(t), bytes = new Uint8Array(1024 * 1024);
    for (let i = 0;i < 20;i++)
    {
        f.proxy.receive(bytes, false, i);
    }
    assert.equal(f.worker.messages.filter(item => item.message.type === 'frame').length, 8);
    assert.equal(f.fallbacks.length, 1);
    assert.match(f.fallbacks[0], /queue/);
});

test('accepted frames free admission capacity and worker diagnostics update without UI rendering', t =>
{
    const f = fixture(t), bytes = new Uint8Array(1024 * 1024);
    for (let i = 0;i < 20;i++)
    {
        f.proxy.receive(bytes, false, i);
        f.worker.emit({ type: 'accepted', bytes: bytes.length });
    }
    assert.equal(f.fallbacks.length, 0);
    f.worker.emit({ type: 'stats', presented: 30, delay: 8, hasDisplayed: true, renderer: 'WebGL', decodeDelay: 3, drawCost: 2, flushes: 1, replays: 4 });
    assert.equal(f.proxy.presented, 30);
    assert.equal(f.proxy.renderer, 'Worker · WebGL');
    assert.equal(f.proxy.delay, 8);
    assert.equal(f.proxy.hasDisplayed, true);
    assert.equal(f.proxy.decodeDelay, 3);
    assert.equal(f.proxy.drawCost, 2);
    assert.equal(f.proxy.replays, 4);
});

test('startup and later worker hangs have finite recovery deadlines', t =>
{
    const f = fixture(t);
    f.worker.emit({ type: 'ready' });
    f.advance(3100);
    f.proxy.tick();
    assert.equal(f.fallbacks.length, 1);
    assert.match(f.fallbacks[0], /stopped responding/);
    f.proxy.tick();
    assert.equal(f.fallbacks.length, 1);
});

test('closed workers release resources before termination and ignore stale callbacks', t =>
{
    const f = fixture(t);
    f.proxy.close();
    assert.equal(f.worker.messages.at(-1).message.type, 'close');
    f.worker.emit({ type: 'refresh', reason: 'late' });
    f.worker.emit({ type: 'backend-failed', reason: 'late' });
    assert.equal(f.refreshes.length, 0);
    assert.equal(f.fallbacks.length, 0);
    f.worker.emit({ type: 'closed' });
    assert.equal(f.worker.terminated, 1);
    assert.equal(f.timers.filter(Boolean).length, 0);
});

test('missing worker shutdown acknowledgment cannot retain an abandoned worker', t =>
{
    const f = fixture(t);
    f.proxy.close();
    f.timers.find(Boolean)();
    assert.equal(f.worker.terminated, 1);
});

test('a stale monitor cannot reconfigure the current connection', t =>
{
    const f = fixture(t);
    f.inactive();
    f.worker.emit({ type: 'refresh', reason: 'old' });
    f.worker.emit({ type: 'fallback', reason: 'old' });
    assert.equal(f.refreshes.length, 0);
    assert.equal(f.codecs.length, 0);
});

test('the compatible renderer retains physical dimensions and closes without retained frame resources', () =>
{
    const calls = [];
    const canvas = { width: 640, height: 480,

        /** 試験用canvasの描画境界を返します。
         * @returns drawImageを含む結果です。
         */
        getContext(kind)
        {
            assert.equal(kind, '2d');
            return {
                /** 試験用canvasへ渡された映像を記録します。 */
                drawImage(...args)
                {
                    calls.push(args);
                } };
        } };
    const renderer = createDesktopRenderer(canvas);
    const frame = {};
    renderer.draw(frame);
    renderer.close();
    assert.deepEqual(calls, [[frame, 0, 0, 640, 480]]);
});
