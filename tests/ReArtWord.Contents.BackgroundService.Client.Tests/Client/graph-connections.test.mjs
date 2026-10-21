import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { setImmediate as settled } from 'node:timers/promises';

const edgeSource = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-edge-scroll.js', import.meta.url), 'utf8');
const edgeModule = 'data:text/javascript;base64,' + Buffer.from(edgeSource).toString('base64');
const snapSource = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-port-snap.js', import.meta.url), 'utf8');
const snapModule = 'data:text/javascript;base64,' + Buffer.from(snapSource).toString('base64');
const source = (await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/graph-connections.js', import.meta.url), 'utf8'))
    .replace('./graph-edge-scroll.js', edgeModule).replace('./graph-port-snap.js', snapModule);
const { attachGraphConnections } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

/** 描画面の座標とイベントを試験用に再現します。 */
class Surface
{
    listeners = new Map();
    dataset = {};
    children = [];
    style = {};
    rect = { left: 10, top: 10, width: 14, height: 14 };
    r = { baseVal: { value: 7 } };
    classes = new Set();
    classList = {
        /** 候補の強調を記録します。 */
        add: name =>
        {
            this.classes.add(name);
        },

        /** 候補の強調を解除します。 */
        remove: name =>
        {
            this.classes.delete(name);
        }
    };
    scrollLeft = 0; scrollTop = 0; scrollWidth = 2000; scrollHeight = 2000; clientWidth = 500; clientHeight = 500;
    clientLeft = 0; clientTop = 0; zoom = 1;

    /** 試験で呼び出せるイベントハンドラーを登録します。 */
    addEventListener(name, handler)
    {
        this.listeners.set(name, handler);
    }

    /** 登録した試験用イベントハンドラーを解除します。 */
    removeEventListener(name)
    {
        this.listeners.delete(name);
    }

    /** 指定したイベントを登録済みのハンドラーへ渡します。
     * @returns 呼出し元が使用する値です。
     */
    dispatch(name, fields = {})
    {
        const event = { target: this, pointerId: 1, button: 0, isPrimary: true, clientX: 20, clientY: 20,

            /** 既定動作を取り消したことを記録します。 */
            preventDefault()
            {
                this.prevented = true;
            },

            /** イベント伝搬を止めたことを記録します。 */
            stopPropagation()
            {
                this.stopped = true;
            },

            /** 後続のハンドラーを止めたことを記録します。 */
            stopImmediatePropagation()
            {
                this.stopped = true;
            },

            ...fields };
        this.listeners.get(name)?.(event);
        return event;
    }

    /** 指定した要素が自分または子孫かを判定します。
     * @returns 呼出し元が使用する値です。
     */
    contains(child)
    {
        return child === this || child.owner === this;
    }

    /** 条件に一致する試験用DOM要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    querySelector()
    {
        return this;
    }

    /** 条件に一致する試験用DOM要素を列挙します。
     * @returns 呼出し元が使用する値です。
     */
    querySelectorAll(selector)
    {
        if (selector === '[data-wire-port]')
        {
            return this.ports ?? [];
        }
        return this.animatedWires ?? [];
    }

    /** 条件に一致する自分または祖先の要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    closest()
    {
        return this.dataset.wirePort ? this : null;
    }

    /** 試験で指定した画面上の矩形を返します。
     * @returns left・top・width・heightを含む結果です。
     */
    getBoundingClientRect()
    {
        return { ...this.rect, left: this.rect.left - (this.owner?.parentElement?.scrollLeft ?? 0), top: this.rect.top - (this.owner?.parentElement?.scrollTop ?? 0) };
    }

    /** 試験の座標変換に使用する行列を返します。
     * @returns inverseを含む結果です。
     */
    getScreenCTM()
    {
        return { inverse: () => ({ left: this.parentElement.scrollLeft, top: this.parentElement.scrollTop, zoom: this.zoom }) };
    }

    /** 捕捉したポインターの識別子を記録します。 */
    setPointerCapture(id)
    {
        this.capture = id;
    }

    /** 指定したポインターを捕捉中か確認します。
     * @returns 呼出し元が使用する判定結果です。
     */
    hasPointerCapture(id)
    {
        return this.capture === id;
    }

    /** 試験で保持したポインターの捕捉を解除します。 */
    releasePointerCapture()
    {
        this.capture = null;
    }

    /** 試験用DOMへ子要素を追加します。 */
    appendChild(child)
    {
        child.owner = this;
        this.children.push(child);
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute(key, value)
    {
        this[key] = value;
    }

    /** 親との関係を解除して試験用DOMから取り除きます。 */
    remove()
    {
        this.owner.children = this.owner.children.filter(child => child !== this);
    }
}

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(output = true)
{
    globalThis.window = new Surface();
    window.innerWidth = window.innerHeight = 1000;
    const frames = new Map();
    let frameId = 0, time = 0;
    globalThis.requestAnimationFrame = fn =>
    {
        frames.set(++frameId, fn);
        return frameId;
    };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    globalThis.DOMPoint = class
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(x, y)
        {
            this.x = x;
            this.y = y;
        }

        /** 試験の変換行列を座標へ適用します。
         * @returns x・yを含む結果です。
         */
        matrixTransform(m)
        {
            return { x: (this.x + m.left) / m.zoom, y: (this.y + m.top) / m.zoom };
        }
    };
    const svg = new Surface();
    svg.parentElement = new Surface();
    svg.parentElement.getBoundingClientRect = () => ({ left: 0, top: 0 });
    svg.dataset.connectionVersion = '7';
    const start = new Surface();
    start.owner = svg;
    start.dataset = { wireNode: 'start', wirePort: 'value', wireOutput: String(output) };
    const target = new Surface();
    target.owner = svg;
    target.dataset = { wireNode: 'target', wirePort: 'value', wireOutput: String(!output) };
    globalThis.document = Object.assign(new Surface(), { elementFromPoint: () => target,

        createElementNS: () => new Surface() });
    const calls = [], checks = [], approval = { value: true };
    const controller = attachGraphConnections(svg, {
        /** 同期のブラウザー呼出しと引数を試験側へ記録します。
         * @returns 呼出し元が使用する値です。
         */
        invokeMethodAsync(...args)
        {
            if (args[0] === 'CanConnectPorts')
            {
                checks.push(args);
                return Promise.resolve(approval.value);
            }
            calls.push(args);
            return Promise.resolve();
        } });

    /** 試験対象の操作を開始します。 */
    const begin = () =>
    {
        svg.dispatch('pointerdown', { target: start });
        window.dispatch('pointermove', { clientX: 100 });
    };

    /** 保持している入力または描画要求を送出します。 */
    const flush = () =>
    {
        time += 16;
        for (const [id, fn] of [...frames])
        {
            frames.delete(id);
            fn(time);
        }
    };

    return { svg, start, target, calls, checks, approval, controller, begin, frames, flush };
}

/** 円の実際の位置と画面外・空白の区別を持つ近接配線の試験を作ります。
 * @returns 接続候補と画面の試験境界です。
 */
function snapFixture(output = true, zoom = 1)
{
    const f = fixture(output);
    f.svg.zoom = zoom;
    f.target.rect = { left: 200 - 7 * zoom, top: 100 - 7 * zoom, width: 14 * zoom, height: 14 * zoom };
    f.svg.ports = [f.start, f.target];
    document.elementFromPoint = (x, y) =>
    {
        if (x < 0 || 500 < x || y < 0 || 500 < y)
        {
            return null;
        }
        return f.svg.ports.find(port =>
        {
            const rect = port.getBoundingClientRect();
            return rect.left <= x && x <= rect.left + rect.width && rect.top <= y && y <= rect.top + rect.height;
        }) ?? f.svg;
    };
    return f;
}

for (const output of [true, false])
{
    for (const zoom of [.05, .5, 1, 1.5])
    {
        test(`compatible ${output ? 'input' : 'output'} snaps inside the scaled margin at zoom ${zoom}`, async () =>
        {
            const f = snapFixture(output, zoom), x = 200 + 7 * zoom + 5 * Math.min(1, zoom);
            f.begin();
            window.dispatch('pointermove', { clientX: x, clientY: 100, buttons: 1 });
            await settled();
            assert.equal(f.target.classes.has('wire-snap-target'), true);
            const path = f.svg.children[0].d;
            assert.ok(output ? path.endsWith(`${200 / zoom} ${100 / zoom}`) : path.startsWith(`M ${200 / zoom} ${100 / zoom} C `));
            for (let i = 0; i < 5; i++)
            {
                window.dispatch('pointermove', { clientX: x - i / 10, clientY: 100, buttons: 1 });
            }
            await settled();
            assert.equal(f.checks.length, 1, 'hovering does not repeatedly validate the whole workflow');
            window.dispatch('pointerup', { clientX: x, clientY: 100 });
            await settled();
            assert.deepEqual(f.calls, [['ConnectPortsAsync', 'start', 'value', output, 'target', 'value', 7]]);
            assert.equal(f.target.classes.size, 0);
            assert.equal(f.svg.children.length, 0);
            f.controller.dispose();
        });
    }
}

for (const zoom of [.05, .5, 1, 1.5])
{
    test(`zoom ${zoom} does not retain an oversized snap region`, async () =>
    {
        const f = snapFixture(true, zoom), x = 200 + 7 * zoom + 6 * Math.min(1, zoom) + .2;
        f.begin();
        window.dispatch('pointermove', { clientX: x, clientY: 100, buttons: 1 });
        await settled();
        assert.equal(f.target.classes.size, 0);
        assert.equal(f.checks.length, 0);
        window.dispatch('pointerup', { clientX: x, clientY: 100 });
        await settled();
        assert.deepEqual(f.calls, [['CancelPortConnectionAsync']]);
        f.controller.dispose();
    });
}

for (const mode of ['too-far', 'incompatible', 'ambiguous', 'occluded', 'outside', 'same-direction'])
{
    test(`proximity ${mode} does not attract or commit`, async () =>
    {
        const f = snapFixture();
        let x = 212;
        if (mode === 'too-far')
        {
            x = 214;
        }
        if (mode === 'incompatible')
        {
            f.approval.value = false;
        }
        if (mode === 'same-direction')
        {
            f.target.dataset.wireOutput = 'true';
        }
        if (mode === 'ambiguous')
        {
            const neighbor = new Surface();
            neighbor.owner = f.svg;
            neighbor.dataset = { ...f.target.dataset, wirePort: 'other' };
            neighbor.rect = { ...f.target.rect, top: 97 };
            f.svg.ports.push(neighbor);
        }
        if (mode === 'occluded' || mode === 'outside')
        {
            document.elementFromPoint = (px, py) => mode === 'outside' || px === 200 && py === 100 ? new Surface() : f.svg;
        }
        f.begin();
        window.dispatch('pointermove', { clientX: x, clientY: 100, buttons: 1 });
        await settled();
        assert.equal(f.target.classes.size, 0);
        window.dispatch('pointerup', { clientX: x, clientY: 100 });
        await settled();
        assert.deepEqual(f.calls, [['CancelPortConnectionAsync']]);
        f.controller.dispose();
    });
}

test('direct port intent wins over an ambiguous nearby neighbor', async () =>
{
    const f = snapFixture();
    const neighbor = new Surface();
    neighbor.owner = f.svg;
    neighbor.dataset = { ...f.target.dataset, wirePort: 'other' };
    neighbor.rect = { ...f.target.rect, top: 94 };
    f.svg.ports.push(neighbor);
    f.begin();
    window.dispatch('pointermove', { clientX: 200, clientY: 100, buttons: 1 });
    await settled();
    assert.equal(f.target.classes.has('wire-snap-target'), true);
    window.dispatch('pointerup', { clientX: 200, clientY: 100 });
    assert.deepEqual(f.calls, [['ConnectPortsAsync', 'start', 'value', true, 'target', 'value', 7]]);
    f.controller.dispose();
});

test('scrolling uses current screen coordinates and leaving the margin releases the snap', async () =>
{
    const f = snapFixture();
    f.begin();
    window.dispatch('pointermove', { clientX: 212, clientY: 100, buttons: 1 });
    await settled();
    assert.equal(f.target.classes.has('wire-snap-target'), true);
    f.svg.parentElement.scrollTop = 20;
    f.svg.parentElement.dispatch('scroll');
    assert.equal(f.target.classes.size, 0);
    window.dispatch('pointermove', { clientX: 212, clientY: 80, buttons: 1 });
    assert.equal(f.target.classes.has('wire-snap-target'), true);
    assert.ok(f.svg.children[0].d.endsWith('200 100'));
    window.dispatch('pointermove', { clientX: 230, clientY: 80, buttons: 1 });
    assert.equal(f.target.classes.size, 0);
    assert.ok(f.svg.children[0].d.endsWith('230 100'));
    f.controller.dispose();
});

for (const ending of ['accept', 'escape', 'blur', 'changed-draft', 'dispose', 'new-pointer'])
{
    test(`a fast proximity drop with delayed validation respects ${ending}`, async () =>
    {
        const f = snapFixture();
        let approve;
        f.approval.value = new Promise(resolve =>
        {
            approve = resolve;
        });
        f.begin();
        window.dispatch('pointerup', { clientX: 212, clientY: 100 });
        f.svg.dispatch('lostpointercapture');
        if (ending === 'escape')
        {
            window.dispatch('keydown', { key: 'Escape' });
        }
        if (ending === 'blur')
        {
            window.dispatch('blur');
        }
        if (ending === 'changed-draft')
        {
            f.svg.dataset.connectionVersion = '8';
            f.controller.update();
        }
        if (ending === 'dispose')
        {
            f.controller.dispose();
        }
        if (ending === 'new-pointer')
        {
            window.dispatch('pointerdown', { pointerId: 2 });
        }
        approve(true);
        await settled();
        assert.equal(f.calls.some(call => call[0] === 'ConnectPortsAsync'), ending === 'accept');
        assert.equal(f.target.classes.size, 0);
        assert.equal(f.svg.children.length, 0);
        assert.equal(f.frames.size, 0);
        f.controller.dispose();
    });
}

for (const output of [true, false])
{
    test(`a stationary ${output ? 'output' : 'input'} wire drag scrolls and redraws at the current zoom`, () =>
    {
        const f = fixture(output), viewport = f.svg.parentElement;
        f.svg.zoom = 2;
        f.begin();
        window.dispatch('pointermove', { clientX: 499, clientY: 499, buttons: 1 });
        f.flush();
        const first = viewport.scrollLeft;
        for (let i = 0; i < 8; i++)
        {
            f.flush();
        }
        assert.ok((first) < (viewport.scrollLeft) && (0) < (first));
        assert.equal(viewport.scrollLeft, viewport.scrollTop);
        const expected = (499 + viewport.scrollLeft) / 2;
        const path = f.svg.children[0].d;
        assert.ok(output ? path.endsWith(`${expected} ${expected}`) : path.startsWith(`M ${expected} ${expected} C `));
        assert.deepEqual(f.calls, [], 'scrolling does not invoke .NET or mutate the document');
        let dropPoint;
        document.elementFromPoint = (x, y) =>
        {
            dropPoint = [x, y];
            return f.target;
        };
        window.dispatch('pointerup', { clientX: 499, clientY: 499 });
        assert.deepEqual(dropPoint, [499, 499]);
        assert.deepEqual(f.calls, [['ConnectPortsAsync', 'start', 'value', output, 'target', 'value', 7]]);
        assert.equal(f.frames.size, 0);
        f.controller.dispose();
    });

    test(`drag from ${output ? 'output' : 'input'} previews then commits once`, () =>
    {
        const f = fixture(output);
        f.begin();
        assert.equal(f.svg.children.length, 1);
        assert.match(f.svg.children[0].d, /^M .* C /);
        window.dispatch('pointerup');
        assert.deepEqual(f.calls, [['ConnectPortsAsync', 'start', 'value', output, 'target', 'value', 7]]);
        assert.equal(f.svg.children.length, 0);
        assert.equal(f.svg.capture, null);
        assert.equal(window.dispatch('click').stopped, true);
        f.controller.dispose();
    });
}

test('wire scrolling clamps at the top/left and extends right/bottom display space', () =>
{
    const f = fixture(), viewport = f.svg.parentElement;
    viewport.scrollLeft = viewport.scrollTop = 8;
    f.begin();
    window.dispatch('pointermove', { clientX: 1, clientY: 1 });
    for (let i = 0; i < 5; i++)
    {
        f.flush();
    }
    assert.equal(viewport.scrollLeft, 0);
    assert.equal(viewport.scrollTop, 0);
    assert.equal(f.svg.style.marginLeft, undefined);
    assert.equal(f.svg.style.marginTop, undefined);
    viewport.scrollLeft = viewport.scrollTop = 1500;
    window.dispatch('pointermove', { clientX: 499, clientY: 499 });
    f.flush();
    assert.ok((0) < (parseFloat(f.svg.style.marginRight)));
    assert.ok((0) < (parseFloat(f.svg.style.marginBottom)));
    window.dispatch('pointermove', { clientX: 250, clientY: 250 });
    const stopped = viewport.scrollLeft;
    for (let i = 0; i < 5; i++)
    {
        f.flush();
    }
    assert.equal(viewport.scrollLeft, stopped);
    assert.equal(f.frames.size, 0);
    f.controller.dispose();
});

for (const end of ['escape', 'pointercancel', 'lostpointercapture', 'blur', 'visibility', 'document', 'buttons', 'dispose'])
{
    test(`wire edge scrolling stops on ${end} and cannot commit a stale drop`, () =>
    {
        const f = fixture(), viewport = f.svg.parentElement;
        f.begin();
        window.dispatch('pointermove', { clientX: 499, clientY: 499 });
        f.flush();
        switch (end)
        {
            case 'escape':
                {
                    window.dispatch('keydown', { key: 'Escape' });
                }
                break;
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
                }
                break;
            case 'buttons':
                {
                    window.dispatch('pointermove', { buttons: 0 });
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
        const stopped = viewport.scrollLeft;
        for (let i = 0; i < 5; i++)
        {
            f.flush();
        }
        assert.equal(viewport.scrollLeft, stopped);
        assert.equal(f.frames.size, 0);
        window.dispatch('pointerup');
        assert.ok(!f.calls.some(call => call[0] === 'ConnectPortsAsync'));
        f.controller.dispose();
        assert.equal(viewport.listeners.size, 0);
        assert.equal(document.listeners.size, 0);
    });
}

test('port clicks and unrelated pointers do not start edge scrolling', () =>
{
    const f = fixture();
    f.svg.dispatch('pointerdown', { target: f.start, clientX: 499, clientY: 499 });
    window.dispatch('pointermove', { pointerId: 2, clientX: 499, clientY: 499 });
    for (let i = 0; i < 5; i++)
    {
        f.flush();
    }
    assert.equal(f.frames.size, 0);
    assert.equal(f.svg.parentElement.scrollLeft, 0);
    assert.equal(window.dispatch('pointerup').stopped, undefined);
    f.controller.dispose();
});

for (const mode of ['escape', 'outside', 'same-direction', 'changed-draft', 'pointercancel', 'blur'])
{
    test(`${mode} cancels without committing or removing the original wire`, async () =>
    {
        const f = fixture();
        const original = new Surface();
        f.svg.appendChild(original);
        f.begin();
        if (mode === 'escape')
        {
            window.dispatch('keydown', { key: 'Escape' });
        }
        if (mode === 'pointercancel' || mode === 'blur')
        {
            window.dispatch(mode);
        }
        if (mode === 'outside')
        {
            document.elementFromPoint = () => null;
            window.dispatch('pointerup');
        }
        if (mode === 'same-direction')
        {
            f.target.dataset.wireOutput = 'true';
            window.dispatch('pointerup');
        }
        if (mode === 'changed-draft')
        {
            f.svg.dataset.connectionVersion = '8';
            f.controller.update();
        }
        await settled();
        assert.deepEqual(f.calls, [['CancelPortConnectionAsync']]);
        assert.deepEqual(f.svg.children, [original]);
        f.controller.dispose();
    });
}

test('a click still bubbles to the existing click-click editor', () =>
{
    const f = fixture();
    f.svg.dispatch('pointerdown', { target: f.start });
    assert.equal(window.dispatch('pointerup').stopped, undefined);
    assert.equal(window.dispatch('click').stopped, undefined);
    assert.deepEqual(f.calls, []);
    f.controller.dispose();
});

test('unchanged graph refreshes retain a dragged connection until release', () =>
{
    const f = fixture();
    f.begin();
    for (let refresh = 0; refresh < 5; refresh++)
    {
        f.controller.update();
        window.dispatch('pointermove', { clientX: 100 + refresh });
        assert.deepEqual(f.calls, []);
        assert.equal(f.svg.children.length, 1);
    }
    window.dispatch('pointerup');
    assert.deepEqual(f.calls, [['ConnectPortsAsync', 'start', 'value', true, 'target', 'value', 7]]);
    f.controller.dispose();
});

test('node drag and non-primary pointers cannot begin a wire gesture', () =>
{
    const f = fixture();
    f.svg.dataset.nodeDragging = 'true';
    f.begin();
    assert.equal(f.svg.children.length, 0);
    f.svg.dataset.nodeDragging = 'false';
    f.svg.dispatch('pointerdown', { target: f.start, isPrimary: false });
    window.dispatch('pointermove', { clientX: 100 });
    assert.equal(f.svg.children.length, 0);
    f.controller.dispose();
});

test('Escape followed by release on the starting port cannot reopen click-click selection', () =>
{
    const f = fixture();
    f.begin();
    window.dispatch('keydown', { key: 'Escape' });
    document.elementFromPoint = () => f.start;
    window.dispatch('pointerup', { target: f.start });
    assert.equal(window.dispatch('click', { target: f.start }).stopped, true);
    assert.deepEqual(f.calls, [['CancelPortConnectionAsync']]);
    window.dispatch('pointerdown', { target: f.start });
    assert.equal(window.dispatch('click', { target: f.start }).stopped, undefined);
    f.controller.dispose();
});

test('blur clears the .NET click-click selection even without a pointer gesture', () =>
{
    const f = fixture();
    window.dispatch('blur');
    assert.deepEqual(f.calls, [['CancelPortConnectionAsync']]);
    f.controller.dispose();
});

test('a new pointer operation after a lost release is not swallowed', () =>
{
    const f = fixture();
    f.begin();
    window.dispatch('blur');
    window.dispatch('pointerdown');
    assert.equal(window.dispatch('pointerup').stopped, undefined);
    assert.equal(window.dispatch('click').stopped, undefined);
    f.controller.dispose();
});

test('only visible wires animate and render refreshes retain visibility without re-observing', t =>
{
    const previous = globalThis.IntersectionObserver;
    let observer;
    globalThis.IntersectionObserver = class
    {
        observed = new Set();

        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(callback)
        {
            this.callback = callback;
            observer = this;
        }

        /** 試験対象の要素を監視対象として記録します。 */
        observe(target)
        {
            this.observed.add(target);
        }

        /** 対象の監視を解除します。 */
        unobserve(target)
        {
            this.observed.delete(target);
        }

        /** 接続と描画を終了し、保留中の解放処理を保持します。 */
        disconnect()
        {
            this.observed.clear();
        }
    };
    t.after(() =>
    {
        globalThis.IntersectionObserver = previous;
    });
    const f = fixture();
    const classes = new Set();
    const wire = { classList: {
        /** 選択値を反転して表示へ反映します。 */
        toggle(name, on)
        {
            if (on)
            {
                classes.add(name);
            }
            else
            {
                classes.delete(name);
            }
        },

        /** 親との関係を解除して試験用DOMから取り除きます。 */
        remove(name)
        {
            classes.delete(name);
        } } };
    f.svg.animatedWires = [wire];
    f.controller.update();
    assert.equal(observer.observed.size, 1);
    assert.equal(classes.has('wire-visible'), false);
    observer.callback([{ target: wire, isIntersecting: true }]);
    assert.equal(classes.has('wire-visible'), true);
    classes.clear();

    // A .NET render replaces the class attribute.
    f.controller.update();
    assert.equal(classes.has('wire-visible'), true);
    assert.equal(observer.observed.size, 1);
    observer.callback([{ target: wire, isIntersecting: false }]);
    assert.equal(classes.has('wire-visible'), false);
    f.svg.animatedWires = [];
    f.controller.update();
    assert.equal(observer.observed.size, 0);
    f.controller.dispose();
});

test('browsers without visibility observation retain enabled animation and cleanup', () =>
{
    const f = fixture();
    const classes = new Set();
    f.svg.animatedWires = [{ classList: {
        /** 選択値を反転して表示へ反映します。 */
        toggle(name, on)
        {
            if (on)
            {
                classes.add(name);
            }
        },

        /** 親との関係を解除して試験用DOMから取り除きます。 */
        remove(name)
        {
            classes.delete(name);
        } } }];
    f.controller.update();
    assert.equal(classes.has('wire-visible'), true);
    f.controller.dispose();
    assert.equal(classes.size, 0);
});
