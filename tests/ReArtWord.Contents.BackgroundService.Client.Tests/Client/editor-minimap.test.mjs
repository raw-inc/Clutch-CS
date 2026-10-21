import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attachMinimapPointer, fitMapRectangle } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/editor-minimap.js';

test('selection uses the smaller fit ratio and respects zoom limits', () =>
{
    assert.deepEqual(fitMapRectangle({ x: -200, y: 100, width: 2000, height: 400 }, 1000, 800), { x: 800, y: 300, zoom: .5 });
    assert.deepEqual(fitMapRectangle({ x: 100, y: -200, width: 400, height: 2000 }, 1000, 800), { x: 300, y: 800, zoom: .4 });
    assert.equal(fitMapRectangle({ x: 0, y: 0, width: 200000, height: 100000 }, 1000, 800).zoom, .05);
    assert.equal(fitMapRectangle({ x: 0, y: 0, width: 10, height: 10 }, 1000, 800).zoom, 1.5);
    assert.equal(fitMapRectangle({ x: 0, y: 0, width: 0, height: 10 }, 1000, 800), null);
    assert.equal(fitMapRectangle({ x: NaN, y: 0, width: 10, height: 10 }, 1000, 800), null);
});

test('drag pans during pointer movement and shift drag selects without panning', t =>
{
    const fixture = pointerFixture(t), { map, state, flush } = fixture;
    map.fire('pointerdown', { clientX: 20, clientY: 30 });
    assert.deepEqual(state.moves.at(-1), { x: 200, y: 300 });
    map.fire('pointermove', { clientX: 50, clientY: 60 });
    map.fire('pointermove', { clientX: 70, clientY: 80 });
    assert.equal(state.moves.length, 1);
    flush();
    assert.deepEqual(state.moves.at(-1), { x: 700, y: 800 });
    assert.equal(state.captured, 1);
    map.fire('pointerup', { clientX: 90, clientY: 95 });
    assert.deepEqual(state.moves.at(-1), { x: 900, y: 950 });
    assert.equal(state.captured, null);
    const count = state.moves.length;
    map.fire('pointerdown', { clientX: 80, clientY: 70, shiftKey: true });
    map.fire('pointermove', { clientX: 20, clientY: 10 });
    flush();
    assert.equal(state.moves.length, count);
    assert.deepEqual(state.rectangle, { x: '200', y: '100', width: '600', height: '600', visibility: 'visible' });
    map.fire('pointerup', { clientX: 20, clientY: 10 });
    assert.deepEqual(state.selected, [{ x: 200, y: 100, width: 600, height: 600 }]);
    assert.equal(state.rectangle.visibility, 'hidden');
});

test('cancel, document changes and disposal never commit a stale selection', t =>
{
    const { map, state, flush, binding } = pointerFixture(t);
    map.fire('pointerdown', { shiftKey: true });
    map.fire('pointermove', { clientX: 80, clientY: 80, pointerId: 2 });
    flush();
    assert.equal(state.rectangle.width, '0');
    map.fire('pointermove', { clientX: 80, clientY: 80 });
    state.version++;
    flush();
    map.fire('pointerup', { clientX: 80, clientY: 80 });
    assert.equal(state.selected.length, 0);
    map.fire('pointerdown', { shiftKey: true });
    map.fire('pointercancel');
    map.fire('pointerup', { clientX: 80, clientY: 80 });
    assert.equal(state.selected.length, 0);
    map.fire('pointerdown', { shiftKey: true });
    map.fire('pointerup', { clientX: 1, clientY: 1 });
    assert.equal(state.selected.length, 0);
    state.enabled = false;
    map.fire('pointerdown');
    assert.equal(state.captured, null);
    state.enabled = true;
    map.fire('pointerdown');
    map.fire('pointermove', { clientX: 80, clientY: 80 });
    const count = state.moves.length;
    binding.dispose();
    flush();
    assert.equal(state.moves.length, count);
    assert.equal(state.captured, null);
    assert.equal(map.events.size, 0);
});

/** 実ポインターの寿命と描画予約だけを再現します。
 * @returns 任意の順序で通知できる境界です。
 */
function pointerFixture(t)
{
    const names = ['DOMPoint', 'document', 'window', 'requestAnimationFrame', 'cancelAnimationFrame'];
    const previous = Object.fromEntries(names.map(name => [name, globalThis[name]]));
    const frames = new Map(), state = { moves: [], selected: [], rectangle: {}, captured: null, enabled: true, version: 0 };
    let sequence = 0;
    const events = new Map();
    const map = { events, viewBox: { baseVal: { x: 0, y: 0, width: 1000, height: 1000 } },

        addEventListener: (name, handler) => events.set(name, handler),

        removeEventListener: name => events.delete(name),

        setPointerCapture: id =>
        {
            state.captured = id;
        },

        hasPointerCapture: id => state.captured === id,

        releasePointerCapture: () =>
        {
            state.captured = null;
        },

        getScreenCTM: () => ({ inverse: () => ({ scale: 10 }) }),

        fire: (name, fields = {}) => events.get(name)?.({ pointerId: 1, button: 0, clientX: 0, clientY: 0, isPrimary: true,

            preventDefault: () =>
            {},

            stopPropagation: () =>
            {},

            ...fields }) };
    globalThis.DOMPoint = class
    {
        /** 画面上の座標を保持します。 */
        constructor(x, y)
        {
            this.x = x;
            this.y = y;
        }

        /** マップの縮尺からワークフロー座標へ戻します。
         * @returns 変換した点です。
         */
        matrixTransform(matrix)
        {
            return { x: this.x * matrix.scale, y: this.y * matrix.scale };
        }
    };
    globalThis.document = globalThis.window = { addEventListener: () =>
    {},

    removeEventListener: () =>
    {} };
    globalThis.requestAnimationFrame = callback =>
    {
        frames.set(++sequence, callback);
        return sequence;
    };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    const binding = attachMinimapPointer(map, { setAttribute: (key, value) =>
    {
        state.rectangle[key] = value;
    } }, {
        enabled: () => state.enabled,

        version: () => state.version,

        move: point => state.moves.push(point),

        select: value => state.selected.push(value) });
    t.after(() =>
    {
        binding.dispose();
        Object.assign(globalThis, previous);
    });
    return { map, state, binding,

        flush: () =>
        {
            for (const [id, callback] of [...frames])
            {
                frames.delete(id);
                callback();
            }
        } };
}
