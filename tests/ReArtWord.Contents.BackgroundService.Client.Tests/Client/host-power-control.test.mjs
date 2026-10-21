import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attachHostPowerControl } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-power-control.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t, state = {})
{
    const doc = new EventTarget(), motion = new EventTarget(), timers = new Map(), calls = [], animations = [];
    let serial = 0;
    doc.defaultView = { matchMedia: () => motion,

        setTimeout: (fn, delay) =>
        {
            timers.set(++serial, { fn, delay });
            return serial;
        },

        clearTimeout: id => timers.delete(id) };

    /** 画面要素の状態とイベントを試験用に再現します。 */
    class Element extends EventTarget
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(parent, dataset = {})
        {
            super();
            this.parent = parent;
            this.dataset = dataset;
            this.attributes = {};
            this.hidden = false;
        }

        /** 指定した要素が自分または子孫かを判定します。
         * @returns 呼出し元が使用する値です。
         */
        contains(node)
        {
            return node === this || !!node?.parent && this.contains(node.parent);
        }

        /** 属性の値を試験用DOMへ記録します。 */
        setAttribute(key, value)
        {
            this.attributes[key] = value;
        }

        /** 操作対象の入力欄へフォーカスを移します。 */
        focus()
        {
            doc.activeElement = this;
        }

        /** 試験で終了を制御できるアニメーションを作成します。
         * @returns 呼出し元が使用する値です。
         */
        animate(frames, options)
        {
            const a = { frames, options,

                /** 進行中の操作を取り消して一時状態を片付けます。 */
                cancel()
                {
                    this.cancelled = true;
                } };
            animations.push(a);
            return a;
        }
    }

    const root = new Element(), trigger = new Element(root), panel = new Element(root), choices = new Element(panel), confirmation = new Element(panel), symbol = new Element(root, { powerAnimation: 'ready' }), outside = new Element();
    const actions = ['restart', 'shutdown', 'remove', 'wake'].map(powerAction => new Element(choices, { powerAction }));
    const confirms = ['restart', 'shutdown', 'remove', 'wake'].map(powerConfirm => new Element(confirmation, { powerConfirm }));
    panel.hidden = true;
    root.ownerDocument = doc;
    root.querySelector = s => ({ '[data-power-trigger]': trigger, '.power-panel': panel, '[data-power-choices]': choices, '[data-power-confirmation]': confirmation, '[data-power-animation]': symbol }[s]);
    root.querySelectorAll = s => s === '[data-power-action]' ? actions : confirms;
    const control = attachHostPowerControl(root, { invokeMethodAsync: async(...args) => calls.push(args) });
    const current = { hostId: 'host', targetIdentity: 'cluster/peer/instance', status: 'Ready', offline: false, stale: false, busy: false, ...state };
    control.update(current);
    t.after(() => control.dispose());

    /** 試験で指定したイベントを発火させます。
     * @returns 呼出し元が使用する値です。
     */
    const fire = (element, name, properties = {}) =>
    {
        const event = new Event(name, { cancelable: true });
        for (const [key, value] of Object.entries(properties))
        {
            Object.defineProperty(event, key, { value });
        }
        element.dispatchEvent(event);
        return event;
    };

    return { doc, motion, timers, calls, animations, root, trigger, panel, choices, confirmation, actions, confirms, outside, symbol, control, current, fire };
}

test('choice requires a second explicit confirmation and only submits once', async t =>
{
    const f = fixture(t);
    f.fire(f.root, 'pointerenter');
    assert.equal(f.panel.hidden, true);
    f.fire(f.trigger, 'click');
    assert.equal(f.panel.hidden, false);
    f.fire(f.actions[0], 'click');
    assert.equal(f.choices.hidden, true);
    assert.equal(f.confirmation.hidden, false);
    assert.equal(f.calls.length, 0);
    assert.equal(f.confirms[0].hidden, false);
    assert.equal(f.confirms[1].hidden, true);
    f.fire(f.confirms[0], 'click');
    f.fire(f.confirms[0], 'click');
    await new Promise(resolve =>
    {
        setImmediate(resolve);
    });
    assert.deepEqual(f.calls, [['ConfirmAsync', 'restart', 'host', 'cluster/peer/instance']]);
    assert.equal(f.panel.hidden, true);
});
test('same button, outside click, Escape, and ten seconds after pointer departure dismiss', t =>
{
    const f = fixture(t);
    f.fire(f.trigger, 'click');
    f.fire(f.trigger, 'click');
    assert.equal(f.panel.hidden, true);
    f.fire(f.trigger, 'click');
    f.fire(f.doc, 'pointerdown', { target: f.outside });
    assert.equal(f.panel.hidden, true);
    f.fire(f.trigger, 'click');
    f.fire(f.root, 'keydown', { key: 'Escape' });
    assert.equal(f.panel.hidden, true);
    assert.equal(f.doc.activeElement, f.trigger);
    f.fire(f.root, 'pointerenter');
    f.fire(f.trigger, 'click');
    f.fire(f.actions[1], 'click');
    assert.equal(f.timers.size, 0);
    f.fire(f.root, 'pointerleave');
    assert.equal(f.timers.size, 1);
    assert.equal([...f.timers.values()][0].delay, 10000);
    f.fire(f.root, 'pointerenter');
    assert.equal(f.timers.size, 0);
    f.fire(f.root, 'pointerleave');
    [...f.timers.values()][0].fn();
    assert.equal(f.panel.hidden, true);
});
test('target replacement and state changes invalidate pending confirmation', async t =>
{
    const f = fixture(t);
    f.fire(f.trigger, 'click');
    f.fire(f.actions[1], 'click');
    f.control.update({ ...f.current, targetIdentity: 'new-instance' });
    f.fire(f.confirms[1], 'click');
    await Promise.resolve();
    assert.equal(f.calls.length, 0);
    f.control.update({ ...f.current, status: 'Joining' });
    f.fire(f.trigger, 'click');
    assert.equal(f.panel.hidden, true);
    f.control.update({ ...f.current, status: 'Syncing' });
    f.fire(f.trigger, 'click');
    assert.equal(f.panel.hidden, false);
    f.control.update({ ...f.current, busy: true });
    assert.equal(f.panel.hidden, true);
});
test('offline permits confirmed removal without enabling shutdown', async t =>
{
    const f = fixture(t, { offline: true, targetIdentity: null });
    f.fire(f.trigger, 'click');
    f.fire(f.actions[0], 'click');
    assert.equal(f.confirmation.hidden, true);
    f.fire(f.actions[2], 'click');
    f.fire(f.confirms[2], 'click');
    await new Promise(resolve =>
    {
        setImmediate(resolve);
    });
    assert.deepEqual(f.calls, [['ConfirmAsync', 'remove', 'host', null]]);
});

test('wake requires confirmation while offline and is invalidated by a returning peer', async t =>
{
    const f = fixture(t, { offline: true, targetIdentity: null });
    f.fire(f.trigger, 'click');
    f.fire(f.actions[3], 'click');
    assert.equal(f.calls.length, 0);
    f.fire(f.confirms[3], 'click');
    await new Promise(setImmediate);
    assert.deepEqual(f.calls, [['ConfirmAsync', 'wake', 'host', null]]);
    f.control.update({ ...f.current, offline: false, targetIdentity: 'new-instance' });
    f.fire(f.trigger, 'click');
    f.fire(f.actions[3], 'click');
    f.fire(f.confirms[3], 'click');
    await new Promise(setImmediate);
    assert.equal(f.calls.length, 1);
});
test('Ready animations share the timeline even when a card mounts later; reduced motion stops them', t =>
{
    const a = fixture(t), b = fixture(t);
    assert.equal(a.animations[0].startTime, 0);
    assert.equal(b.animations[0].startTime, 0);
    assert.equal(a.animations[0].options.duration, 4200);
    a.symbol.dataset.powerAnimation = 'syncing';
    a.control.update({ ...a.current, status: 'Syncing' });
    assert.equal(a.animations[0].cancelled, true);
    assert.equal(a.animations.length, 1);
    a.symbol.dataset.powerAnimation = 'ready';
    a.control.update(a.current);
    a.motion.matches = true;
    a.fire(a.motion, 'change');
    assert.equal(a.animations[1].cancelled, true);
    a.control.dispose();
    a.fire(a.trigger, 'click');
    assert.equal(a.panel.hidden, true);
});
