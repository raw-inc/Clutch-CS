import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const edgeSource = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-edge-scroll.js', import.meta.url), 'utf8');
const edgeModule = 'data:text/javascript;base64,' + Buffer.from(edgeSource).toString('base64');
const viewportSource = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-viewport.js', import.meta.url), 'utf8');
const viewportModule = 'data:text/javascript;base64,' + Buffer.from(viewportSource).toString('base64');
const scopeSource = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-scope-highlight.js', import.meta.url), 'utf8');
const scopeModule = 'data:text/javascript;base64,' + Buffer.from(scopeSource).toString('base64');
const source = (await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-gestures.js', import.meta.url), 'utf8')).replace('./graph-edge-scroll.js', edgeModule).replace('./graph-viewport.js', viewportModule).replace('./graph-scope-highlight.js', scopeModule);
const { attachGraphGestures } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

/** 描画面の座標とイベントを試験用に再現します。 */
class Surface
{
    listeners = new Map();
    dataset = { connectionVersion: '7', nodeDragging: 'false' };
    style = {};
    attributes = {};
    children = [];
    classList = { contains: () => false };
    scrollLeft = 0; scrollTop = 0; scrollWidth = 2000; scrollHeight = 2000; clientWidth = 500; clientHeight = 500;
    clientLeft = 0; clientTop = 0;

    /** 試験で呼び出せるイベントハンドラーを登録します。 */
    addEventListener(name, handler)
    {
        this.listeners.set(name, handler);
    }

    /** 登録した試験用イベントハンドラーを解除します。 */
    removeEventListener(name, handler)
    {
        if (this.listeners.get(name) === handler)
        {
            this.listeners.delete(name);
        }
    }

    /** 指定したイベントを登録済みのハンドラーへ渡します。 */
    dispatch(name, fields = {})
    {
        const event = { target: this, pointerId: 1, button: 0, isPrimary: true, clientX: 10, clientY: 20,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {},

            /** イベント伝搬を止めたことを記録します。 */
            stopPropagation()
            {},

            ...fields };
        this.listeners.get(name)?.(event);
    }

    /** 条件に一致する自分または祖先の要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    closest()
    {
        return null;
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute(name, value)
    {
        this.attributes[name] = value;
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(child)
    {
        this.children.push(child);
    }

    /** 親との関係を解除して試験用DOMから取り除きます。 */
    remove()
    {
        this.removed = true;
    }

    /** 条件に一致する試験用DOM要素を列挙します。
     * @returns 呼出し元が使用する値です。
     */
    querySelectorAll()
    {
        return this.nodes ?? [];
    }

    /** 条件に一致する試験用DOM要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    querySelector()
    {
        return this;
    }

    /** 指定したポインターを捕捉中か確認します。
     * @returns 呼出し元が使用する判定結果です。
     */
    hasPointerCapture(id)
    {
        return this.capture === id;
    }

    /** 捕捉したポインターの識別子を記録します。 */
    setPointerCapture(id)
    {
        this.capture = id;
    }

    /** 試験で保持したポインターの捕捉を解除します。 */
    releasePointerCapture()
    {
        this.capture = null;
    }

    /** 操作対象の入力欄へフォーカスを移します。 */
    focus()
    {}

    /** 試験で指定した画面上の矩形を返します。
     * @returns left・top・right・bottomを含む結果です。
     */
    getBoundingClientRect()
    {
        return { left: 0, top: 0, right: 500, bottom: 500 };
    }
}

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t)
{
    const old = Object.fromEntries(['window', 'document', 'Element', 'MouseEvent', 'requestAnimationFrame', 'cancelAnimationFrame', 'setTimeout', 'clearTimeout'].map(key => [key, globalThis[key]]));
    const timers = new Map(), frames = new Map(), calls = [];
    let id = 0, release, time = 0;
    Object.assign(globalThis, {
        Element: Surface,
        MouseEvent: Event,
        window: new Surface(),
        document: new Surface(),

        setTimeout: fn =>
        {
            timers.set(++id, fn);
            return id;
        },

        clearTimeout: key => timers.delete(key),

        requestAnimationFrame: fn =>
        {
            frames.set(++id, fn);
            return id;
        },

        cancelAnimationFrame: key => frames.delete(key)
    });
    window.innerWidth = window.innerHeight = 1000;
    document.createElementNS = () => new Surface();
    window.contentsBrowser = { point: (svg, x, y) => ({ x: (x + svg.parentElement.scrollLeft) / 2, y: (y + svg.parentElement.scrollTop) / 2 }) };
    const svg = new Surface();
    svg.parentElement = new Surface();
    const controller = attachGraphGestures(svg, {
        /** 同期のブラウザー呼出しと引数を試験側へ記録します。
         * @returns 呼出し元が使用する値です。
         */
        invokeMethodAsync(...args)
        {
            calls.push(args);
            if (args[0] === 'MovePointer')
            {
                return new Promise(resolve =>
                {
                    release = resolve;
                });
            }
            return Promise.resolve();
        }
    }, 1);
    t.after(() =>
    {
        controller.dispose();
        Object.assign(globalThis, old);
    });

    /** 保持している入力または描画要求を送出します。 */
    const flush = () =>
    {
        time += 16;
        for (const [key, fn] of [...timers])
        {
            timers.delete(key);
            fn();
        }
        for (const [key, fn] of [...frames])
        {
            frames.delete(key);
            fn(time);
        }
    };

    /** 試験対象のドラッグ開始イベントを生成します。 */
    const startDrag = (x, y) =>
    {
        const node = new Surface();
        node.closest = selector => selector === '.graph-node' ? node : null;
        svg.parentElement.dispatch('pointerdown', { target: node, clientX: 250, clientY: 250 });
        svg.setPointerCapture(1);
        svg.dataset.nodeDragging = 'true';
        svg.dispatch('pointermove', { clientX: x, clientY: y, buttons: 1 });
        controller.update(1);
    };

    /** 保留している試験用の要求を完了させます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const completeAsync = async () =>
    {
        release?.();
        await Promise.resolve();
        await Promise.resolve();
        await Promise.resolve();
    };

    return { svg, calls, controller, timers, frames, flush, complete: completeAsync, startDrag };
}

test('a motionless graph schedules no pointer calls or animation frames', t =>
{
    const f = fixture(t);
    for (let i = 0; i < 20; i++)
    {
        f.controller.update(1);
    }
    assert.equal(f.calls.length, 0);
    assert.equal(f.frames.size, 0);
    assert.equal(f.timers.size, 0);
});

test('Ctrl wheel can shrink the graph below forty percent down to five percent', t =>
{
    const f = fixture(t);
    f.controller.update(.4);
    f.svg.parentElement.dispatch('wheel', { ctrlKey: true, deltaY: 10000 });
    f.flush();
    assert.deepEqual(f.calls, [['ZoomAtAsync', .05]]);
});

test('Ctrl wheel keeps the five percent lower bound and the existing upper bound', t =>
{
    const f = fixture(t);
    f.controller.update(.05);
    f.svg.parentElement.dispatch('wheel', { ctrlKey: true, deltaY: 10000 });
    f.flush();
    assert.equal(f.calls.length, 0);
    f.svg.parentElement.dispatch('wheel', { ctrlKey: true, deltaY: -10000 });
    f.flush();
    assert.deepEqual(f.calls, [['ZoomAtAsync', 1.5]]);
});

test('pointer bursts coalesce to the latest point with only one call in flight', async t =>
{
    const f = fixture(t);
    f.svg.dataset.realtimePointer = 'true';
    for (let i = 0; i < 100; i++)
    {
        f.svg.dispatch('pointermove', { clientX: i });
    }
    assert.equal(f.timers.size, 1);
    f.flush();
    assert.deepEqual(f.calls, [['MovePointer', 49.5, 10, 7]]);
    for (let i = 100; i < 200; i++)
    {
        f.svg.dispatch('pointermove', { clientX: i });
    }
    assert.equal(f.timers.size, 0);
    await f.complete();
    f.flush();
    assert.deepEqual(f.calls[1], ['MovePointer', 99.5, 10, 7]);
    await f.complete();
});

test('queued positions retain their document version and leave clears pending movement', async t =>
{
    const f = fixture(t);
    f.svg.dataset.realtimePointer = 'true';
    f.svg.dispatch('pointermove');
    f.svg.dataset.connectionVersion = '8';
    f.flush();
    assert.equal(f.calls[0][3], 7);
    f.svg.dispatch('pointermove', { clientX: 90 });
    f.svg.dispatch('pointerleave');
    await f.complete();
    f.flush();
    assert.deepEqual(f.calls.map(call => call[0]), ['MovePointer', 'LeavePointer']);
});

test('panning and wire connection gestures do not enqueue node or presence motion', t =>
{
    const f = fixture(t);
    f.svg.dataset.connectionDragging = 'true';
    f.svg.dispatch('pointermove');
    f.svg.dataset.connectionDragging = 'false';
    f.svg.parentElement.dispatch('pointerdown');
    f.svg.dispatch('pointermove');
    assert.equal(f.timers.size, 0);
    assert.equal(f.calls.length, 0);
});

test('disposal cancels queued work and late completions cannot schedule more', async t =>
{
    const f = fixture(t);
    f.svg.dataset.realtimePointer = 'true';
    f.svg.dispatch('pointermove');
    f.flush();
    f.svg.dispatch('pointermove');
    f.controller.dispose();
    await f.complete();
    assert.equal(f.timers.size, 0);
    assert.equal(f.frames.size, 0);
    assert.equal(f.svg.listeners.size, 0);
    assert.equal(f.svg.parentElement.listeners.size, 0);
    assert.equal(window.listeners.size, 0);
    assert.equal(document.listeners.size, 0);
    assert.equal(f.calls.length, 1);
});

test('holding a dragged node at an edge continues scrolling and supplies transformed coordinates', async t =>
{
    const f = fixture(t), viewport = f.svg.parentElement;
    f.startDrag(498, 498);
    f.flush();
    const firstScroll = viewport.scrollLeft;
    assert.ok((0) < (firstScroll));
    for (let i = 0; i < 10; i++)
    {
        f.flush();
    }
    assert.ok((firstScroll) < (viewport.scrollLeft));
    assert.equal(viewport.scrollLeft, viewport.scrollTop);
    assert.equal(f.calls.filter(call => call[0] === 'MovePointer').length, 1, 'only one .NET call may be in flight');
    await f.complete();
    f.flush();
    const last = f.calls.at(-1);
    assert.equal(last[0], 'MovePointer');
    assert.equal(last[1], (498 + viewport.scrollLeft) / 2, 'coordinates use the updated scroll transform and zoom');
    assert.equal(last[2], (498 + viewport.scrollTop) / 2);
    assert.equal(last[3], 7);
});

test('left and top stop at the canvas origin without adding empty margins', t =>
{
    const f = fixture(t), viewport = f.svg.parentElement;
    viewport.scrollLeft = viewport.scrollTop = 10;
    f.startDrag(1, 1);
    for (let i = 0; i < 10; i++)
    {
        f.flush();
    }
    assert.equal(viewport.scrollLeft, 0);
    assert.equal(viewport.scrollTop, 0);
    assert.equal(f.svg.style.marginLeft, undefined);
    assert.equal(f.svg.style.marginTop, undefined);
    assert.equal(f.frames.size, 0);
});

test('right and bottom extend the canvas and moving back to the center stops scrolling', t =>
{
    const f = fixture(t), viewport = f.svg.parentElement;
    viewport.scrollLeft = viewport.scrollTop = 1500;
    f.startDrag(499, 499);
    f.flush();
    assert.ok((0) < (parseFloat(f.svg.style.marginRight)));
    assert.ok((0) < (parseFloat(f.svg.style.marginBottom)));
    f.svg.dispatch('pointermove', { clientX: 250, clientY: 250, buttons: 1 });
    const left = viewport.scrollLeft, top = viewport.scrollTop;
    for (let i = 0; i < 5; i++)
    {
        f.flush();
    }
    assert.equal(viewport.scrollLeft, left);
    assert.equal(viewport.scrollTop, top);
    assert.equal(f.frames.size, 0);
});

for (const end of ['pointerup', 'pointercancel', 'lostpointercapture', 'blur', 'visibility', 'document', 'dispose'])
{
    test(`auto scroll ends on ${end} before another frame can move the viewport`, async t =>
    {
        const f = fixture(t), viewport = f.svg.parentElement;
        f.startDrag(499, 499);
        f.flush();
        switch (end)
        {
            case 'lostpointercapture':
                {
                    f.svg.dispatch(end);
                }
                break;
            case 'visibility':
                {
                    document.hidden = true;
                    document.dispatch('visibilitychange');
                }
                break;
            case 'document':
                {
                    f.svg.dataset.connectionVersion = '8';
                    f.controller.update(1);
                }
                break;
            case 'dispose':
                {
                    f.controller.dispose();
                }
                break;
            default:
                {
                    window.dispatch(end);
                }
                break;
        }
        const left = viewport.scrollLeft, top = viewport.scrollTop;
        await f.complete();
        for (let i = 0; i < 10; i++)
        {
            f.flush();
        }
        assert.equal(viewport.scrollLeft, left);
        assert.equal(viewport.scrollTop, top);
        assert.equal(f.frames.size, 0);
    });
}

test('a click without movement and an unaccepted drag never auto scroll', t =>
{
    const f = fixture(t), viewport = f.svg.parentElement;
    const node = new Surface();
    node.closest = selector => selector === '.graph-node' ? node : null;
    viewport.dispatch('pointerdown', { target: node, clientX: 499, clientY: 499 });
    f.svg.setPointerCapture(1);
    f.svg.dataset.nodeDragging = 'true';
    f.controller.update(1);
    f.flush();
    assert.equal(viewport.scrollLeft, 0);
    f.svg.releasePointerCapture();
    f.svg.dispatch('pointermove', { clientX: 490, clientY: 490, buttons: 1 });
    f.flush();
    assert.equal(viewport.scrollLeft, 0);
    assert.equal(f.frames.size, 0);
});

test('default pointer sharing publishes click and edit coordinates without sending hover motion', async t =>
{
    const f = fixture(t);
    for (let i = 0; i < 100; i++)
    {
        f.svg.dispatch('pointermove', { clientX: i });
    }
    f.flush();
    assert.equal(f.calls.length, 0);
    assert.equal(f.timers.size, 0);
    f.svg.dispatch('pointerdown', { clientX: 80, clientY: 100 });
    assert.deepEqual(f.calls.at(-1), ['InteractPointerAsync', 40, 50, 7, null]);
    f.svg.dispatch('input', { clientX: undefined, clientY: undefined });
    assert.deepEqual(f.calls.at(-1), ['InteractPointerAsync', 40, 50, 7, null]);
    f.svg.dispatch('pointerleave');
    assert.equal(f.calls.filter(call => call[0] === 'LeavePointer').length, 0);
    await f.complete();
    f.flush();
    assert.equal(f.frames.size, 0);
});

test('Shift marquee selects enclosed nodes additively with reverse drag and zoomed coordinates', t =>
{
    const f = fixture(t), viewport = f.svg.parentElement;
    const inside = new Surface(), partial = new Surface();
    inside.dataset.nodeId = 'inside';
    partial.dataset.nodeId = 'partial';
    inside.getBoundingClientRect = () => ({ left: 100, top: 100, right: 200, bottom: 200 });
    partial.getBoundingClientRect = () => ({ left: 400, top: 400, right: 600, bottom: 600 });
    f.svg.nodes = [inside, partial];
    viewport.dispatch('pointerdown', { target: f.svg, shiftKey: true, clientX: 500, clientY: 500 });
    viewport.dispatch('pointermove', { target: f.svg, clientX: 0, clientY: 0 });
    const rectangle = f.svg.children[0];
    assert.equal(rectangle.attributes.width, 250);
    assert.equal(rectangle.attributes.height, 250);
    assert.equal(viewport.scrollLeft, 0);
    viewport.dispatch('pointerup', { target: f.svg, clientX: 0, clientY: 0 });
    assert.deepEqual(f.calls.at(-1), ['SelectRangeAsync', ['inside'], 7]);
    assert.ok(rectangle.removed);
    assert.ok(!f.calls.some(call => call[0] === 'BackgroundClickAsync'));
});

for (const cancellation of ['pointercancel', 'escape'])
{
    test(`marquee ${cancellation} leaves the selection unchanged`, t =>
    {
        const f = fixture(t), viewport = f.svg.parentElement;
        viewport.dispatch('pointerdown', { target: f.svg, shiftKey: true });
        viewport.dispatch('pointermove', { clientX: 400, clientY: 400 });
        if (cancellation === 'escape')
        {
            viewport.dispatch('keydown', { key: 'Escape' });
        }
        else
        {
            viewport.dispatch('pointercancel');
        }
        viewport.dispatch('pointerup', { clientX: 400, clientY: 400 });
        assert.ok(f.svg.children[0].removed);
        assert.ok(!f.calls.some(call => call[0] === 'SelectRangeAsync' || call[0] === 'BackgroundClickAsync'));
    });
}

test('Ctrl click never starts node edge scrolling or clears the selected node', t =>
{
    const f = fixture(t), node = new Surface();
    node.dataset.nodeId = 'toggle';
    node.closest = selector => selector === '.graph-node' ? node : null;
    f.svg.parentElement.dispatch('pointerdown', { target: node, ctrlKey: true });
    f.svg.dispatch('pointerdown', { target: node, ctrlKey: true });
    f.svg.dispatch('pointerup', { target: node, ctrlKey: true });
    assert.equal(f.frames.size, 0);
    assert.ok(f.calls.filter(call => call[0] === 'InteractPointerAsync').every(call => call[4] === null));
});

test('Ctrl+C/V use the latest pointer and current transform without requiring realtime presence', t =>
{
    const f = fixture(t);
    f.svg.dataset.canCopy = 'true';
    f.svg.dispatch('pointermove', { clientX: 120, clientY: 80 });
    let prevented = 0;
    const key = { ctrlKey: true,

        preventDefault: () => prevented++ };
    f.svg.parentElement.dispatch('keydown', { ...key, key: 'c' });
    f.svg.parentElement.scrollLeft = 40;
    f.svg.parentElement.scrollTop = 20;
    f.svg.parentElement.dispatch('keydown', { ...key, key: 'V' });
    assert.equal(prevented, 2);
    assert.deepEqual(f.calls, [['ClipboardShortcutAsync', false, 60, 40, 7], ['ClipboardShortcutAsync', true, 80, 50, 7]]);
    assert.equal(f.timers.size, 0);
});

test('clipboard shortcuts leave text editing, IME, alternate chords and active drags alone', t =>
{
    const f = fixture(t);
    f.svg.dataset.canCopy = 'true';
    let prevented = 0;
    const key = { key: 'v', ctrlKey: true,

        preventDefault: () => prevented++ };
    const field = new Surface();
    field.closest = selector => selector.includes('input,textarea') ? field : null;
    f.svg.parentElement.dispatch('keydown', { ...key, target: field });
    for (const ignored of [{ isComposing: true }, { repeat: true }, { altKey: true }, { shiftKey: true }, { defaultPrevented: true }, { ctrlKey: false }])
    {
        f.svg.parentElement.dispatch('keydown', { ...key, ...ignored });
    }
    f.svg.dataset.nodeDragging = 'true';
    f.svg.parentElement.dispatch('keydown', key);
    f.svg.dataset.nodeDragging = 'false';
    f.svg.dataset.connectionDragging = 'true';
    f.svg.parentElement.dispatch('keydown', key);
    assert.equal(prevented, 0);
    assert.deepEqual(f.calls, []);
});

test('keyboard-only paste uses the visible center, empty copy is native, and disposal removes shortcuts', t =>
{
    const f = fixture(t);
    let prevented = 0;
    const key = { ctrlKey: true,

        preventDefault: () => prevented++ };
    f.svg.parentElement.dispatch('keydown', { ...key, key: 'c' });
    assert.equal(prevented, 0);
    f.svg.dispatch('pointermove', { clientX: 50, clientY: 60 });
    f.svg.dispatch('pointerleave');
    f.svg.parentElement.dispatch('keydown', { key: 'v', metaKey: true,

        preventDefault: () => prevented++ });
    assert.deepEqual(f.calls, [['ClipboardShortcutAsync', true, 125, 125, 7]]);
    f.controller.dispose();
    f.svg.parentElement.dispatch('keydown', { ...key, key: 'v' });
    assert.equal(f.calls.length, 1);
});

test('double click keeps the card identity when pointer capture retargets it to the canvas', t =>
{
    const f = fixture(t), node = new Surface();
    node.dataset.nodeId = 'recipe';
    node.closest = selector => selector === '.graph-node' ? node : null;
    f.svg.parentElement.dispatch('pointerdown', { target: node });
    f.svg.setPointerCapture(1);
    f.svg.dispatch('pointerup');
    f.svg.dispatch('dblclick');
    assert.deepEqual(f.calls.at(-1), ['OpenRecipePreviewAsync', 'recipe', 7]);
    f.svg.dispatch('dblclick');
    assert.equal(f.calls.filter(call => call[0] === 'OpenRecipePreviewAsync').length, 1);
});

test('double click in an input or background does not open the last card', t =>
{
    const f = fixture(t), field = new Surface();
    field.closest = () => field;
    f.svg.parentElement.dispatch('pointerdown', { target: field });
    f.svg.dispatch('dblclick');
    f.svg.parentElement.dispatch('pointerdown', { target: f.svg });
    f.svg.dispatch('dblclick');
    assert.ok(!f.calls.some(call => call[0] === 'OpenRecipePreviewAsync'));
});

test('logical name editing does not open a recipe and IME keeps its confirmation key', t =>
{
    const f = fixture(t), title = new Surface(), input = new Surface();
    title.dataset.nodeId = 'recipe';
    title.closest = selector => ['.graph-node', '.node-name'].includes(selector) ? title : null;
    f.svg.parentElement.dispatch('pointerdown', { target: title });
    f.svg.dispatch('dblclick', { target: title });
    assert.ok(!f.calls.some(call => call[0] === 'OpenRecipePreviewAsync'));
    input.closest = selector => selector === '.node-name-input' ? input : null;
    let stopped = 0, prevented = 0;
    for (const composition of [{ isComposing: true }, { keyCode: 229 }])
    {
        f.svg.parentElement.dispatch('keydown', { target: input, key: 'Enter', ...composition,

            stopPropagation: () => stopped++,

            preventDefault: () => prevented++ });
    }
    assert.equal(stopped, 2);
    assert.equal(prevented, 0);
    assert.deepEqual(f.calls, []);
});

test('a captured logical name double click reaches its label once without opening a recipe', t =>
{
    const f = fixture(t), title = new Surface(), delivered = [];
    title.dataset.nodeId = 'recipe';
    title.isConnected = true;
    title.closest = selector => ['.graph-node', '.node-name'].includes(selector) ? title : null;
    title.dispatchEvent = event =>
    {
        delivered.push([event.type, event.bubbles]);
        f.svg.dispatch(event.type, { target: title });
    };
    f.svg.parentElement.dispatch('pointerdown', { target: title });
    f.svg.setPointerCapture(1);
    window.dispatch('pointerup');
    f.svg.dispatch('dblclick');
    assert.deepEqual(delivered, [['dblclick', true]]);
    assert.deepEqual(f.calls, []);
    f.svg.dispatch('dblclick');
    assert.equal(delivered.length, 1);
    f.svg.parentElement.dispatch('pointerdown', { target: title });
    f.svg.dispatch('dblclick', { target: title });
    assert.equal(delivered.length, 1, 'a directly targeted label keeps its native double click');
    f.svg.parentElement.dispatch('pointerdown', { target: title });
    title.isConnected = false;
    f.svg.dispatch('dblclick');
    title.isConnected = true;
    f.svg.parentElement.dispatch('pointerdown', { target: title });
    f.svg.dataset.connectionVersion = '8';
    f.svg.dispatch('dblclick');
    assert.equal(delivered.length, 1, 'detached labels and old graph generations cannot enter editing');
});

test('logical name dragging participates in edge scrolling and does not enter editing after movement', t =>
{
    const f = fixture(t), title = new Surface();
    title.dataset.nodeId = 'recipe';
    title.isConnected = true;
    title.closest = selector => ['.graph-node', '.node-name'].includes(selector) ? title : null;
    title.dispatchEvent = () => assert.fail('Dragging must not edit a logical name');
    f.svg.parentElement.dispatch('pointerdown', { target: title, clientX: 250, clientY: 250 });
    f.svg.setPointerCapture(1);
    f.svg.dataset.nodeDragging = 'true';
    f.svg.dispatch('pointermove', { clientX: 498, clientY: 498, buttons: 1 });
    f.controller.update(1);
    f.flush();
    assert.ok((0) < (f.svg.parentElement.scrollLeft));
    f.svg.dispatch('dblclick');
    assert.ok(!f.calls.some(call => call[0] === 'OpenRecipePreviewAsync'));
    window.dispatch('pointerup');
    const stopped = f.svg.parentElement.scrollLeft;
    f.flush();
    assert.equal(f.svg.parentElement.scrollLeft, stopped);
});

test('Delete passes the focused card or wire while marquee focus uses the current selection', t =>
{
    const f = fixture(t), node = new Surface(), wire = new Surface();
    node.dataset.nodeId = 'selected';
    node.closest = selector => selector === '.graph-node' ? node : null;
    wire.dataset.wireIndex = '3';
    wire.closest = selector => selector === '.wire-group' ? wire : null;
    let prevented = 0, stopped = 0;
    for (const target of [node, wire, f.svg.parentElement])
    {
        f.svg.parentElement.dispatch('keydown', { target, key: 'Delete',

            preventDefault: () => prevented++,

            stopPropagation: () => stopped++ });
    }
    assert.deepEqual(f.calls, [['DeleteSelectionAsync', 'selected', null, 7], ['DeleteSelectionAsync', null, 3, 7], ['DeleteSelectionAsync', null, null, 7]]);
    assert.equal(prevented, 3);
    assert.equal(stopped, 3);
});

test('Delete preserves text editing, IME, menus and active gestures', t =>
{
    const f = fixture(t), field = new Surface(), menu = new Surface();
    field.closest = selector => selector.includes('input,textarea') ? field : null;
    menu.closest = selector => selector.includes('.graph-context-menu') ? menu : null;
    let prevented = 0;
    const key = { key: 'Delete',

        preventDefault: () => prevented++ };
    for (const ignored of [{ target: field }, { target: menu }, { isComposing: true }, { repeat: true }, { defaultPrevented: true }, { ctrlKey: true }, { metaKey: true }])
    {
        f.svg.parentElement.dispatch('keydown', { ...key, ...ignored });
    }
    f.svg.dataset.nodeDragging = 'true';
    f.svg.parentElement.dispatch('keydown', key);
    f.svg.dataset.nodeDragging = 'false';
    f.svg.dataset.connectionDragging = 'true';
    f.svg.parentElement.dispatch('keydown', key);
    assert.equal(prevented, 0);
    assert.deepEqual(f.calls, []);
});

test('context menu capture covers fields, media, wires and the viewport and is disposed', t =>
{
    const f = fixture(t), field = new Surface(), wire = new Surface(), menu = new Surface();
    field.dataset.nodeId = 'preview';
    field.closest = selector => selector === '.graph-node' || selector.includes('input,textarea') ? field : null;
    wire.dataset.wireIndex = '2';
    wire.closest = selector => selector === '.wire-group' ? wire : null;
    menu.closest = selector => selector.includes('.graph-context-menu') ? menu : null;
    let prevented = 0, stopped = 0;
    for (const target of [field, wire, f.svg.parentElement, menu])
    {
        f.svg.parentElement.dispatch('contextmenu', { target, clientX: 123, clientY: 456,

            preventDefault: () => prevented++,

            stopPropagation: () => stopped++ });
    }
    assert.deepEqual(f.calls, [['OpenContextMenuAsync', 'preview', null, 123, 456, 7], ['OpenContextMenuAsync', null, 2, 123, 456, 7], ['OpenContextMenuAsync', null, null, 123, 456, 7]]);
    assert.equal(prevented, 3);
    assert.equal(stopped, 3);
    f.controller.dispose();
    f.svg.parentElement.dispatch('contextmenu');
    f.svg.parentElement.dispatch('keydown', { key: 'Delete' });
    assert.equal(f.calls.length, 3);
});
