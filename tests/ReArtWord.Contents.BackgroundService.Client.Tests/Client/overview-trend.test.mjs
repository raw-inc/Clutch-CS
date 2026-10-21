import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/overview-trend.js', import.meta.url), 'utf8');
let importId = 0;

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t, reduce = false)
{
    const clock = { wall: 1_000_000, monotonic: 1_000 };
    t.mock.method(Date, 'now', () => clock.wall);
    t.mock.method(performance, 'now', () => clock.monotonic);
    const originals = new Map(['requestAnimationFrame', 'cancelAnimationFrame', 'matchMedia', 'getComputedStyle', 'ResizeObserver'].map(key => [key, globalThis[key]]));
    const frames = new Map();
    let id = 0;
    const motion = { matches: reduce,

        /** 試験で呼び出せるイベントハンドラーを登録します。 */
        addEventListener(name, listener)
        {
            this.changed = listener;
        } };
    globalThis.matchMedia = () => motion;
    globalThis.requestAnimationFrame = callback =>
    {
        frames.set(++id, callback);
        return id;
    };
    globalThis.cancelAnimationFrame = frame => frames.delete(frame);
    globalThis.getComputedStyle = () => ({ strokeWidth: '1.6px' });
    let resize, disconnected = false;
    globalThis.ResizeObserver = class
    {
        /** 画面サイズの変更を試験から通知します。 */
        constructor(callback)
        {
            resize = callback;
        }

        /** 実レイアウトの購読は行いません。 */
        observe()
        {}

        /** 対象を離れたときの購読解除を記録します。 */
        disconnect()
        {
            disconnected = true;
        }
    };
    const module = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64') + '#' + ++importId);
    const attributes = new Map();
    const blurAttributes = new Map();
    const bandAttributes = new Map();
    const layers = Array.from({ length: 24 }, () => new Map());
    const clipAttributes = new Map();
    let clipWrites = 0;
    const line = { setAttribute: (name, value) => attributes.set(name, value) };
    const blur = { setAttribute: (name, value) => blurAttributes.set(name, value) };
    const band = { setAttribute: (name, value) => bandAttributes.set(name, value) };
    const clip = { setAttribute: (name, value) =>
    {
        clipWrites++;
        clipAttributes.set(name, value);
    } };
    const series = { setAttribute: (name, value) => attributes.set(name, value),

        querySelector: () => line };
    const chart = { isConnected: true, hidden: false, height: 104, width: 300, viewBox: { baseVal: { y: -2, height: 104, width: 300 } },

        querySelector: selector => ({ '[data-shadow-blur]': blur, '[data-shadow-band]': band, '[data-area-clip]': clip })[selector] ?? series,

        querySelectorAll: () => layers.map(attributes => ({ setAttribute: (name, value) => attributes.set(name, value) })),

        getBoundingClientRect: () => ({ height: chart.height, width: chart.width }),

        /** 条件に一致する自分または祖先の要素を取得します。
         * @returns 呼出し元が使用する値です。
         */
        closest()
        {
            return this.hidden ? {} : null;
        } };
    t.after(() =>
    {
        module.unobserve(chart);
        for (const [key, original] of originals)
        {
            if (original === undefined)
            {
                delete globalThis[key];
            }
            else
            {
                globalThis[key] = original;
            }
        }
    });
    return {
        clock, chart, motion, frames, attributes, blurAttributes, bandAttributes, layers, clipAttributes, ...module,

        clipWrites: () => clipWrites,

        resize: () => resize(),

        disconnected: () => disconnected,

        /** 試験用の通信ヘッダーとペイロードを組み立てます。 */
        frame(milliseconds)
        {
            clock.monotonic += milliseconds;
            const callbacks = [...frames.values()];
            frames.clear();
            for (const callback of callbacks)
            {
                callback(clock.monotonic);
            }
        },

        /** 試験対象のバイト位置を計算します。
         * @returns 呼出し元が使用する値です。
         */
        offset()
        {
            return Number(attributes.get('transform').match(/translate\((\S+) 0\)/)[1]);
        }
    };
}

test('sample rebase updates the path and offset before another animation frame can paint', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    f.frame(3000);
    assert.equal(300 + f.offset(), 297);
    f.clock.monotonic += 5;
    f.update(f.chart, 'M294,50 L297,40 L300,60', f.clock.wall + 3000);
    assert.equal(f.attributes.get('d'), 'M294,50 L297,40 L300,60');
    assert.equal(297 + f.offset(), 296.995);
    const afterUpdate = 297 + f.offset();
    f.frame(40);
    assert.ok(297 + f.offset() < afterUpdate, 'the existing vertex must keep moving left');
});

test('a peer clock ahead of the browser does not clamp or rewind the time axis at updates', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall + 2000);
    assert.equal(f.offset(), 2);
    f.frame(3000);
    const beforeUpdate = 300 + f.offset();
    f.update(f.chart, 'M294,50 L297,40 L300,60', f.clock.wall + 5000);
    assert.equal(297 + f.offset(), beforeUpdate);
});

test('wall clock corrections cannot move already rendered samples backwards', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    f.frame(1000);
    assert.equal(f.offset(), -1);
    f.clock.wall -= 10_000;
    f.frame(1000);
    assert.equal(f.offset(), -2);
    f.clock.wall += 20_000;
    f.frame(1000);
    assert.equal(f.offset(), -3);
});

test('a frame timestamp captured before a render cannot rewind its immediate offset', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    f.frame(3000);
    f.clock.monotonic += 40;
    f.update(f.chart, 'M294,50 L297,40 L300,60', f.clock.wall + 3000);
    const offset = f.offset();
    const callbacks = [...f.frames.values()];
    f.frames.clear();
    for (const callback of callbacks)
    {
        callback(f.clock.monotonic - 5);
    }
    assert.equal(f.offset(), offset);
});

test('reduced motion renders updates without retaining an animation loop', async t =>
{
    const f = await fixtureAsync(t, true);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall - 3000);
    assert.equal(f.offset(), 0);
    assert.equal(f.frames.size, 0);
    f.motion.matches = false;
    f.motion.changed();
    assert.equal(f.offset(), -3);
    assert.equal(f.frames.size, 1);
    f.motion.matches = true;
    f.motion.changed();
    assert.equal(f.frames.size, 0);
    assert.equal(f.offset(), 0);
});

test('repeated renders share one loop and disconnected charts release their animation', async t =>
{
    const f = await fixtureAsync(t);
    for (let n = 0; n < 10; n++)
    {
        f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    }
    assert.equal(f.frames.size, 1);
    f.chart.hidden = true;
    f.frame(1000);
    assert.equal(f.offset(), 0);
    f.chart.hidden = false;
    f.frame(1000);
    assert.equal(f.offset(), -2);
    f.chart.isConnected = false;
    f.frame(1000);
    assert.equal(f.frames.size, 0);
});

test('removed elements are released even when reduced motion has stopped the animation loop', async t =>
{
    const f = await fixtureAsync(t, true);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    f.chart.isConnected = false;
    f.unobserve(null);
    f.motion.matches = false;
    f.motion.changed();
    assert.equal(f.frames.size, 0);
    f.update(null, '', null);
    assert.equal(f.frames.size, 0);
});

test('shadow is clipped below each observed interval without bridging gaps or isolated samples', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M-3,0 L0,20 L3,40 M30,50 M60,70 L90,80', f.clock.wall);
    const clips = f.clipAttributes.get('d').trim().split(/\s*Z\s*/).filter(Boolean);
    assert.equal(clips.length, 2, 'softening must remain clipped separately to each measured interval');
    assert.equal(clips[0], 'M-3,0 L0,20 L3,40  L3,102 L-3,102');
    assert.equal(clips[1], 'M60,70 L90,80 L90,102 L60,102');
    assert.equal(f.bandAttributes.get('d').split('M').length - 1, 2, 'the shadow has the same missing intervals as the line');
    f.frame(3000);
    assert.equal(f.offset(), -3);
    assert.equal(f.clipWrites(), 1, 'time-axis animation must not rebuild the shadow clip');
    f.update(f.chart, '', null);
    assert.equal(f.clipAttributes.get('d').trim(), '');
    assert.equal(f.bandAttributes.get('d').trim(), '');
});

test('unchanged samples reuse the shadow clip and a rebase updates both paths together', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    assert.equal(f.clipWrites(), 1);
    f.update(f.chart, 'M294,50 L297,40 L300,60', f.clock.wall + 3000);
    assert.equal(f.attributes.get('d'), 'M294,50 L297,40 L300,60');
    assert.equal(f.clipAttributes.get('d').trim(), 'M294,50 L297,40 L300,60 L300,102 L294,102 Z');
    assert.equal(f.clipWrites(), 2);
});

test('shadow retains a fifteen-line-width falloff when cards resize or become visible', async t =>
{
    const f = await fixtureAsync(t);
    f.chart.height = 0;
    f.update(f.chart, 'M297,50 L300,40', f.clock.wall);
    assert.equal(f.blurAttributes.size, 0, 'hidden cards must not produce infinite filter coordinates');
    for (const height of [104, 120, 150, 260])
    {
        f.chart.height = height;
        f.resize();
        const [horizontal, vertical] = f.blurAttributes.get('stdDeviation').split(' ').map(Number);
        assert.ok(Math.abs(horizontal * f.chart.width / 300 - 0.6) < 0.000001);
        assert.ok(Math.abs(vertical * height / 104 - 0.6) < 0.000001, 'only subpixel seams should be softened');
        const step = Number(f.layers[1].get('transform').match(/translate\(0 (\S+)\)/)[1]);
        assert.ok(Math.abs(step * f.layers.length * height / 104 - 1.6 * 15) < 0.000001);
    }
    assert.equal(f.clipWrites(), 1, 'resizing must not reconstruct unchanged sample geometry');
    f.unobserve(f.chart);
    assert.equal(f.disconnected(), true);
});

test('sloping, flat and sharply descending segments share one constant-depth shadow band', async t =>
{
    const f = await fixtureAsync(t);
    f.update(f.chart, 'M0,80 L30,20 L60,20 L90,90', f.clock.wall);
    assert.equal(f.bandAttributes.get('d').trim(), 'M0,80 L30,20 L60,20 L90,90 L90,91 L60,21 L30,21 L0,81 Z');
    assert.equal(f.bandAttributes.get('d').split('M').length - 1, 1, 'slopes and corners must not create separate point shadows');
    const width = Number(f.layers.at(-1).get('transform').match(/translate\(0 (\S+)\)/)[1]) + 1;
    assert.equal(width, 24);
});
