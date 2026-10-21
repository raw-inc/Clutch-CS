import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attachLanguageMenu } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/language-menu.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t)
{
    const doc = new EventTarget();

    /** 画面要素の状態とイベントを試験用に再現します。 */
    class Element extends EventTarget
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(parent)
        {
            super();
            this.parent = parent;
            this.attributes = {};
        }

        /** 指定した要素が自分または子孫かを判定します。
         * @returns 呼出し元が使用する値です。
         */
        contains(node)
        {
            return node === this || !!node?.parent && this.contains(node.parent);
        }

        /** 属性の値を試験用DOMへ記録します。 */
        setAttribute(name, value)
        {
            this.attributes[name] = value;
        }

        /** 操作対象の入力欄へフォーカスを移します。 */
        focus()
        {
            doc.activeElement = this;
        }
    }

    const root = new Element(), trigger = new Element(root), menu = new Element(root);
    const items = [new Element(menu), new Element(menu)], outside = new Element();
    root.ownerDocument = doc;
    root.querySelector = selector => selector === '[data-language-trigger]' ? trigger : menu;
    menu.querySelectorAll = () => items;
    menu.hidden = true;
    const controller = attachLanguageMenu(root);
    t.after(() => controller.dispose());

    /** 試験で指定したイベントを発火させます。
     * @returns 呼出し元が使用する値です。
     */
    function fire(element, name, properties = {})
    {
        const event = new Event(name, { cancelable: true });
        for (const [key, value] of Object.entries(properties))
        {
            Object.defineProperty(event, key, { value });
        }
        element.dispatchEvent(event);
        return event;
    }

    return { root, trigger, menu, items, outside, doc, controller, fire };
}

test('hover opens without stealing focus; click holds it open until a second click', t =>
{
    const f = fixture(t);
    f.outside.focus();
    f.fire(f.root, 'pointerenter', { pointerType: 'mouse' });
    assert.equal(f.menu.hidden, false);
    assert.equal(f.doc.activeElement, f.outside);
    f.fire(f.trigger, 'click');
    f.fire(f.root, 'pointerleave');
    assert.equal(f.menu.hidden, false);
    f.fire(f.trigger, 'click');
    assert.equal(f.menu.hidden, true);
    assert.equal(f.trigger.attributes['aria-expanded'], 'false');
});
test('touch opens with a click and outside pointer dismisses the menu', t =>
{
    const f = fixture(t);
    f.fire(f.root, 'pointerenter', { pointerType: 'touch' });
    assert.equal(f.menu.hidden, true);
    f.fire(f.trigger, 'click');
    assert.equal(f.menu.hidden, false);
    f.fire(f.doc, 'pointerdown', { target: f.outside });
    assert.equal(f.menu.hidden, true);
});
test('arrow keys wrap, home/end navigate, and Escape restores the trigger', t =>
{
    const f = fixture(t);
    f.trigger.focus();
    assert.equal(f.fire(f.root, 'keydown', { key: 'ArrowUp' }).defaultPrevented, true);
    assert.equal(f.doc.activeElement, f.items[1]);
    f.fire(f.root, 'keydown', { key: 'ArrowDown' });
    assert.equal(f.doc.activeElement, f.items[0]);
    f.fire(f.root, 'keydown', { key: 'End' });
    assert.equal(f.doc.activeElement, f.items[1]);
    f.fire(f.root, 'keydown', { key: 'Home' });
    assert.equal(f.doc.activeElement, f.items[0]);
    f.fire(f.root, 'keydown', { key: 'Escape' });
    assert.equal(f.menu.hidden, true);
    assert.equal(f.doc.activeElement, f.trigger);
});
test('selection and tab departure close; leaving with focus inside keeps choices available', t =>
{
    const f = fixture(t);
    f.fire(f.root, 'pointerenter', { pointerType: 'mouse' });
    f.items[0].focus();
    f.fire(f.root, 'pointerleave');
    assert.equal(f.menu.hidden, false);
    f.fire(f.menu, 'click', { target: f.items[1] });
    assert.equal(f.menu.hidden, true);
    assert.equal(f.doc.activeElement, f.trigger);
    f.fire(f.trigger, 'click');
    f.fire(f.root, 'focusout', { relatedTarget: f.outside });
    assert.equal(f.menu.hidden, true);
});
test('hover departure closes and disposal detaches global listeners', t =>
{
    const f = fixture(t);
    f.fire(f.root, 'pointerenter', { pointerType: 'mouse' });
    f.fire(f.root, 'pointerleave');
    assert.equal(f.menu.hidden, true);
    f.controller.dispose();
    f.fire(f.trigger, 'click');
    assert.equal(f.menu.hidden, true);
});
