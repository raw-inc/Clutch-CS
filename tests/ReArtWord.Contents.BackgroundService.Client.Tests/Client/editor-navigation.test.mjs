import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attachEditorNavigation, clippedMapScope, editorNavigationShortcut } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/editor-navigation.js';
import { centerGraphPoint } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-viewport.js';

test('minimap scope remains outlined when the complete workflow fits inside a zoomed-out viewport', () =>
{
    const bounds = { x: -100, y: -50, width: 5200, height: 3600 };
    assert.deepEqual(clippedMapScope({ x: -10000, y: -6000 }, { x: 10000, y: 6000 }, bounds, 20),
        { x: -80, y: -30, width: 5160, height: 3560 });
    assert.deepEqual(clippedMapScope({ x: 50, y: 80 }, { x: 450, y: 480 }, bounds, 20),
        { x: 50, y: 80, width: 400, height: 400 });
    assert.equal(clippedMapScope({ x: 9000, y: 9000 }, { x: 9500, y: 9500 }, bounds, 20).width, 0);
});

test('closed map and empty search do not inspect the node geometry during graph edits', t =>
{
    const names = ['document', 'window', 'MutationObserver', 'ResizeObserver', 'requestAnimationFrame', 'cancelAnimationFrame'];
    const previous = Object.fromEntries(names.map(name => [name, globalThis[name]]));
    t.after(() =>
    {
        Object.assign(globalThis, previous);
    });
    let geometryReads = 0, mutation;
    const frames = [];
    const events = { addEventListener: () =>
    {},

    removeEventListener: () =>
    {} };
    const mapNodes = { replaceChildren: () =>
        {} }, scope = { setAttribute: () =>
        {} };
    const map = { ...events,

        setAttribute: () =>
        {},

        querySelector: selector => selector === '[data-map-nodes]' ? mapNodes : scope };
    const svg = { querySelectorAll: () => [],

        getScreenCTM: () =>
        {
            geometryReads++;
            return null;
        } };
    const viewport = { ...events, clientLeft: 0, clientWidth: 500,

        querySelector: () => svg,

        getBoundingClientRect: () => ({ left: 20, top: 100, width: 500 }) };
    const root = { style: {},

        setAttribute: () =>
        {},

        querySelector: () => map };
    globalThis.document = { ...events, documentElement: { clientWidth: 1000 } };
    globalThis.window = events;
    globalThis.MutationObserver = class
    {
        /** DOM変更を試験から通知します。 */
        constructor(callback)
        {
            mutation = callback;
        }

        /** 実DOMの購読は行いません。 */
        observe()
        {}

        /** 試験の購読を終了します。 */
        disconnect()
        {}
    };
    globalThis.ResizeObserver = class
    {
        /** 実レイアウトの購読は行いません。 */
        observe()
        {}

        /** 試験の購読を終了します。 */
        disconnect()
        {}
    };
    globalThis.requestAnimationFrame = callback => frames.push(callback);
    globalThis.cancelAnimationFrame = () =>
    {};
    const binding = attachEditorNavigation(viewport, root, {});
    binding.update(false, false, [], null);
    frames.shift()();
    mutation([{ target: svg }]);
    assert.equal(geometryReads, 0);
    assert.equal(frames.length, 0);
    binding.update(true, false, [], null);
    assert.ok((0) < (geometryReads));
    const beforeActivity = geometryReads, beforeFrames = frames.length;
    mutation([{ attributeName: 'data-running', target: { dataset: { nodeId: 'running' },

        hasAttribute: () => true } }]);
    assert.equal(geometryReads, beforeActivity);
    assert.equal(frames.length, beforeFrames);
    binding.dispose();
});

test('map shortcut requires an empty canvas and ordinary character input is preserved', () =>
{
    assert.equal(editorNavigationShortcut({ key: 'm' }, true, false), 0);
    assert.equal(editorNavigationShortcut({ key: 'M' }, true, false), 0);
    assert.equal(editorNavigationShortcut({ key: 'm' }, false, false), null);
    assert.equal(editorNavigationShortcut({ key: 'm', ctrlKey: true }, true, false), null);
    for (const condition of ['repeat', 'isComposing', 'altKey', 'shiftKey', 'defaultPrevented'])
    {
        assert.equal(editorNavigationShortcut({ key: 'm', [condition]: true }, true, false), null);
    }
    assert.equal(editorNavigationShortcut({ key: 'f', ctrlKey: true }, false, true), 1);
    assert.equal(editorNavigationShortcut({ key: 'f', metaKey: true }, false, true), 1);
    assert.equal(editorNavigationShortcut({ key: 'b', ctrlKey: true }, false, false), 2);
    assert.equal(editorNavigationShortcut({ key: 'b', ctrlKey: true }, false, true), null);
});

test('world-coordinate navigation centers equally at five percent and enlarged zoom, including negative positions', t =>
{
    const previous = globalThis.DOMPoint;
    t.after(() =>
    {
        globalThis.DOMPoint = previous;
    });
    globalThis.DOMPoint = class
    {
        /** 座標を保持するブラウザー境界を再現します。 */
        constructor(x, y)
        {
            this.x = x;
            this.y = y;
        }

        /** 指定倍率とスクロールを画面座標へ適用します。
         * @returns 変換した画面位置です。
         */
        matrixTransform(matrix)
        {
            return { x: this.x * matrix.scale + matrix.x, y: this.y * matrix.scale + matrix.y };
        }
    };
    for (const zoom of [0.05, 1, 1.5])
    {
        for (const point of [[4000, 2000], [-1000, -600]])
        {
            const viewport = { scrollLeft: 0, scrollTop: 0, clientLeft: 0, clientTop: 0, clientWidth: 500, clientHeight: 300,
                scrollWidth: 20000, scrollHeight: 20000,

                getBoundingClientRect: () => ({ left: 10, top: 20 }) };
            const svg = { style: {},

                getScreenCTM: () => ({ scale: zoom,
                    x: 10 + (parseFloat(svg.style.marginLeft) || 0) - viewport.scrollLeft,
                    y: 20 + (parseFloat(svg.style.marginTop) || 0) - viewport.scrollTop }) };
            centerGraphPoint(svg, viewport, ...point);
            const centered = new DOMPoint(...point).matrixTransform(svg.getScreenCTM());
            assert.ok(Math.abs(centered.x - 260) < 0.00001);
            assert.ok(Math.abs(centered.y - 170) < 0.00001);
        }
    }
});
