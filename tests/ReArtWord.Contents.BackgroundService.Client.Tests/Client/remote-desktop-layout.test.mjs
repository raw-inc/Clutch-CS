import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopLayout } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-layout.js';
import { mapDesktopPoint } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-input.js';

test('normal and fullscreen layouts fit mixed monitors after labels and chrome without changing input coordinates', t =>
{
    const names = ['document', 'window', 'ResizeObserver', 'getComputedStyle'];
    const before = Object.fromEntries(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    let callback, disconnected = 0;
    const visualEvents = {};
    const root = { style: {}, children: [], parentElement: {},

        getBoundingClientRect: () => ({ top: 140 }) };
    const viewport = { clientWidth: 1200, clientHeight: 600, dataset: {} };
    const views = [[3840, 2160], [1080, 1920], [1920, 1080]].map(([width, height], id) => ({
        monitor: { width, height, id }, panel: { style: {} }, canvas: { width, height, style: {} }, name: { getBoundingClientRect: () => ({ height: 26 }) }
    }));
    Object.assign(globalThis, {
        document: { fullscreenElement: null,

            querySelector: () => ({ getBoundingClientRect: () => ({ height: 26 }) }),

            /** 試験で呼び出せるイベントハンドラーを登録します。 */
            addEventListener()
            {} },
        window: { innerHeight: 1080, scrollY: 0,

            /** 試験で呼び出せるイベントハンドラーを登録します。 */
            addEventListener()
            {},

            visualViewport: { height: 1080, offsetTop: 0,

                /** 試験で呼び出せるイベントハンドラーを登録します。 */
                addEventListener(name, callback)
                {
                    visualEvents[name] = callback;
                } } },
        ResizeObserver: class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor(action)
            {
                callback = action;
            }

            /** 試験対象の要素を監視対象として記録します。 */
            observe()
            {}

            /** 接続と描画を終了し、保留中の解放処理を保持します。 */
            disconnect()
            {
                disconnected++;
            }
        },

        getComputedStyle: () => ({ columnGap: '8px', paddingLeft: '2px', paddingRight: '2px', paddingTop: '2px', paddingBottom: '2px' })
    });
    t.after(() =>
    {
        for (const name of names)
        {
            if (before[name])
            {
                Object.defineProperty(globalThis, name, before[name]);
            }
            else
            {
                delete globalThis[name];
            }
        }
    });
    let mode = 'fit';
    const controller = new AbortController(), layout = createDesktopLayout(root, viewport, () => views, controller.signal, () => mode);
    assert.equal(root.style.height, '902px');
    for (const [width, height, fullscreen] of [[1200, 600, false], [540, 240, false], [1920, 850, true], [800, 500, true]])
    {
        viewport.clientWidth = width;
        viewport.clientHeight = height;
        document.fullscreenElement = fullscreen ? root : null;
        callback();
        assert.equal(root.style.height, fullscreen ? '1080px' : '902px');
        let total = 20;
        for (const view of views)
        {
            const w = parseFloat(view.canvas.style.width), h = parseFloat(view.canvas.style.height);
            assert.ok(h + 30 <= height + 0.001);
            assert.ok(Math.abs(w / h - view.monitor.width / view.monitor.height) < 0.00001);
            total += w;
            assert.equal(view.canvas.width, view.monitor.width);
            assert.equal(view.canvas.height, view.monitor.height);
            const point = new Int32Array(3);
            assert.equal(mapDesktopPoint(view.monitor, { left: 0, top: 0, width: w, height: h }, w / 2, h / 2, point), true);
            assert.deepEqual(Array.from(point), [view.monitor.id, view.monitor.width / 2, view.monitor.height / 2]);
        }
        assert.ok(total <= width + 0.001);
    }
    mode = 'native';
    for (const density of [1, 1.25, 2])
    {
        for (const fullscreen of [false, true])
        {
            window.devicePixelRatio = density;
            document.fullscreenElement = fullscreen ? root : null;
            layout.update();
            for (const view of views)
            {
                const width = parseFloat(view.canvas.style.width), height = parseFloat(view.canvas.style.height);
                assert.equal(width * density, view.monitor.width);
                assert.equal(height * density, view.monitor.height);
                const point = new Int32Array(3);
                assert.equal(mapDesktopPoint(view.monitor, { left: -100, top: -60, width, height }, -100 + width / 2, -60 + height / 2, point), true);
                assert.deepEqual(Array.from(point), [view.monitor.id, view.monitor.width / 2, view.monitor.height / 2]);
            }
        }
    }
    window.visualViewport.height = 440;
    document.fullscreenElement = null;
    visualEvents.resize();
    assert.equal(root.style.height, '262px');
    window.visualViewport.offsetTop = 100;
    visualEvents.scroll();
    assert.equal(root.style.height, '362px');
    window.visualViewport.height = 1080;
    window.visualViewport.offsetTop = 0;
    visualEvents.resize();
    assert.equal(root.style.height, '902px');
    delete window.visualViewport;
    viewport.scrollLeft = 500;
    viewport.scrollTop = 300;
    mode = 'fit';
    layout.update();
    assert.equal(viewport.scrollLeft, 0);
    assert.equal(viewport.scrollTop, 0);
    assert.equal(viewport.dataset.scale, 'fit');
    controller.abort();
    assert.equal(disconnected, 1);
    layout.dispose();
});
