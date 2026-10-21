import { test } from 'node:test';
import assert from 'node:assert/strict';
import { terminalModifiedInput, createTerminalKeys } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/terminal-keys.js';

test('independent modifiers encode printable input and standard VT navigation', () =>
{
    assert.equal(terminalModifiedInput('c', { ctrl: true }), '\x03');
    assert.equal(terminalModifiedInput('c', { ctrl: true, alt: true, shift: true }), '\x1b\x03');
    assert.equal(terminalModifiedInput('x', { shift: true }), 'X');
    assert.equal(terminalModifiedInput(' ', { ctrl: true }), '\0');
    assert.equal(terminalModifiedInput('\t', { shift: true }), '\x1b[Z');
    assert.equal(terminalModifiedInput('\x1bOA', { ctrl: true }), '\x1b[1;5A');
    assert.equal(terminalModifiedInput('\x1b[D', { ctrl: true, alt: true, shift: true }), '\x1b[1;8D');
    assert.equal(terminalModifiedInput('\x1b[3~', { alt: true }), '\x1b[3;3~');
    assert.equal(terminalModifiedInput('\x7f', { ctrl: true }), '\b');
    assert.equal(terminalModifiedInput('\x1bOA', {}), '\x1bOA');
});

test('multi-character paste and terminal reports are never modified', () =>
{
    for (const data of ['echo example', '\x1b[200~c\x1b[201~', '\x1b]10;rgb:11/22/33\x1b\\', '\x1b[?1;2c'])
    {
        assert.equal(terminalModifiedInput(data, { ctrl: true, alt: true, shift: true }), data);
    }
});

test('toggles persist independently, buttons share modifiers, and disconnection clears them', t =>
{
    const saved = Object.fromEntries(['document', 'window', 'matchMedia'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
    const buttons = [], sent = [], events = {};
    let ready = true, application = false;
    globalThis.document = { createElement: () => ({ children: [], attributes: {},

        /** 受け取った項目を保持する順序へ追加します。 */
        append(...items)
        {
            this.children.push(...items);
        },

        /** 属性の値を試験用DOMへ記録します。 */
        setAttribute(key, value)
        {
            this.attributes[key] = value;
        } }) };
    globalThis.window = {
        /** 試験で呼び出せるイベントハンドラーを登録します。 */
        addEventListener(name, callback)
        {
            events[name] = callback;
        } };
    globalThis.matchMedia = () => ({ matches: false,

        /** 試験で呼び出せるイベントハンドラーを登録します。 */
        addEventListener(name, callback)
        {
            events.media = callback;
        } });
    t.after(() =>
    {
        for (const [name, descriptor] of Object.entries(saved))
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
    const controller = new AbortController();
    const ctx = { signal: controller.signal,

        t: en => en,

        /** 操作の実行と処理中表示を持つボタンを構成します。
         * @returns 呼出し元が使用する値です。
         */
        button(label, action)
        {
            const button = { label, onclick: action, attributes: {},

                /** 属性の値を試験用DOMへ記録します。 */
                setAttribute(key, value)
                {
                    this.attributes[key] = value;
                },

                /** 試験で呼び出せるイベントハンドラーを登録します。 */
                addEventListener()
                {} };
            buttons.push(button);
            return button;
        } };
    const keys = createTerminalKeys(ctx, { ready: () => ready,

        send: data => sent.push(data),

        /** 操作対象の入力欄へフォーカスを移します。 */
        focus()
        {},

        applicationCursor: () => application });

    /** クリック対象を確認して選択または操作を行います。
     * @returns 呼出し元が使用する値です。
     */
    const click = label => buttons.find(button => button.label === label).onclick();

    click('Ctrl');
    click('Alt');
    assert.equal(keys.transform('c'), '\x1b\x03');
    assert.equal(keys.transform('d'), '\x1b\x04');
    click('Alt');
    click('←');
    click('Del');
    assert.deepEqual(sent, ['\x1b[1;5D', '\x1b[3;5~']);
    assert.equal(buttons.find(button => button.label === 'Ctrl').attributes['aria-pressed'], 'true');
    ready = false;
    keys.refresh();
    click('Ctrl');
    click('↵');
    assert.equal(sent.length, 2);
    assert.ok(buttons.every(button => button.disabled));
    ready = true;
    keys.refresh();
    application = true;
    click('↑');
    assert.equal(sent.at(-1), '\x1bOA');
    click('Shift');
    events.blur();
    assert.equal(keys.transform('c'), 'c');
    click('Ctrl');
    keys.reset();
    assert.equal(keys.transform('c'), 'c');
});
