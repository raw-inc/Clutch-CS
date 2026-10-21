import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createTerminalFullscreen } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/terminal-fullscreen.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t)
{
    const original = Object.getOwnPropertyDescriptor(globalThis, 'document'), doc = new EventTarget(), controller = new AbortController();
    let active = true, focus = 0, fit = 0, exit = 0;
    doc.fullscreenElement = null;
    doc.exitFullscreen = async() =>
    {
        exit++;
        doc.fullscreenElement = null;
        doc.dispatchEvent(new Event('fullscreenchange'));
    };
    globalThis.document = doc;

    /** 入力検証に使う試験対象を構成します。
     * @returns requestFullscreenを含む結果です。
     */
    const target = () => ({ requestFullscreen: async function()
    {
        doc.fullscreenElement = this;
        doc.dispatchEvent(new Event('fullscreenchange'));
    } });

    const root = target(), screen = target();
    const ctx = { signal: controller.signal,

        t: en => en,

        icon: () => ({}),

        button: (label, action) => ({ label, onclick: action, attrs: {},

            /** 子要素を表示順の先頭へ追加します。 */
            prepend()
            {},

            /** 属性の値を試験用DOMへ記録します。 */
            setAttribute(k, v)
            {
                this.attrs[k] = v;
            } }) };
    const controls = createTerminalFullscreen(root, screen, ctx, { active: () => active,

        focus: () => focus++,

        fit: () => fit++ });
    t.after(() =>
    {
        controls.dispose();
        controller.abort();
        if (original)
        {
            Object.defineProperty(globalThis, 'document', original);
        }
        else
        {
            delete globalThis.document;
        }
    });
    return { root, screen, doc, controls,

        setActive: value =>
        {
            const assignedValue = active = value;
            return assignedValue;
        },

        get fit()
        {
            return fit;
        },

        get focus()
        {
            return focus;
        },

        get exit()
        {
            return exit;
        } };
}

test('workspace and content fullscreen reflect state, resize, and toggle independently', async t =>
{
    const f = fixture(t), [full, content] = f.controls.buttons;
    await full.onclick();
    assert.equal(f.doc.fullscreenElement, f.root);
    assert.equal(full.attrs['aria-pressed'], 'true');
    assert((0) < (f.fit) && (0) < (f.focus));
    await content.onclick();
    assert.equal(f.doc.fullscreenElement, f.screen);
    assert.equal(full.attrs['aria-pressed'], 'false');
    assert.equal(content.attrs['aria-pressed'], 'true');
    await content.onclick();
    assert.equal(f.doc.fullscreenElement, null);
    assert.equal(content.attrs['aria-pressed'], 'false');
});
test('Escape only exits this terminal fullscreen and never becomes terminal input', async t =>
{
    const f = fixture(t);
    await f.controls.buttons[1].onclick();
    const key = new Event('keydown', { cancelable: true });
    key.key = 'Escape';
    f.doc.dispatchEvent(key);
    await Promise.resolve();
    assert(key.defaultPrevented);
    assert.equal(f.doc.fullscreenElement, null);
    f.doc.fullscreenElement = { other: true };
    const other = new Event('keydown', { cancelable: true });
    other.key = 'Escape';
    f.doc.dispatchEvent(other);
    assert.equal(other.defaultPrevented, false);
    assert.equal(f.exit, 1);
});
test('disconnect invalidates an outstanding fullscreen request', async t =>
{
    const f = fixture(t);
    let finish;
    f.root.requestFullscreen = () => new Promise(resolve =>
    {
        finish = () =>
        {
            f.doc.fullscreenElement = f.root;
            resolve();
        };
    });
    const entering = f.controls.buttons[0].onclick();
    f.setActive(false);
    await f.controls.exit();
    finish();
    await entering;
    assert.equal(f.doc.fullscreenElement, null);
    f.controls.update();
    assert(f.controls.buttons.every(b => b.disabled));
});
