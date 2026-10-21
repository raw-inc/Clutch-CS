import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attachPreview } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/node-preview.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns create・elements・revoked・unsupportedを含む結果です。
 */
function fixture(t)
{
    const oldDocument = globalThis.document, oldCreate = URL.createObjectURL, oldRevoke = URL.revokeObjectURL;
    const elements = [], revoked = [], views = [];
    let supported = true, serial = 0;
    globalThis.document = {
        /** 指定された種類の試験用DOM要素を作成します。
         * @returns 呼出し元が使用する値です。
         */
        createElement(tag)
        {
            const item = { tag, events: {},

                /** 試験で指定したメディアの対応状況を返します。
                 * @returns 呼出し元が使用する判定結果です。
                 */
                canPlayType()
                {
                    return supported ? 'probably' : '';
                },

                /** 試験で呼び出せるイベントハンドラーを登録します。 */
                addEventListener(name, action)
                {
                    this.events[name] = action;
                },

                /** 試験対象が一時停止されたことを記録します。 */
                pause()
                {
                    this.paused = true;
                },

                /** 対象の情報を取得し、現在の表示へ反映します。 */
                load()
                {},

                /** 試験用DOMから指定の属性を取り除きます。 */
                removeAttribute(name)
                {
                    delete this[name];
                },

                /** 親との関係を解除して試験用DOMから取り除きます。 */
                remove()
                {
                    this.removed = true;
                } };
            elements.push(item);
            return item;
        } };
    URL.createObjectURL = () => `blob:test-${++serial}`;
    URL.revokeObjectURL = url => revoked.push(url);

    /** 入力された条件から新しい対象を作成します。
     * @returns view・error・contentを含む結果です。
     */
    const create = () =>
    {
        const error = { textContent: '' }, content = {
                /** 受け取った項目を保持する順序へ追加します。 */
                append(element)
                {
                    this.element = element;
                } }, root = {
                /** 条件に一致する試験用DOM要素を取得します。
                 * @returns 呼出し元が使用する値です。
                 */
                querySelector(key)
                {
                    return key === '[data-preview-content]' ? content : error;
                } };
        const view = attachPreview(root);
        views.push(view);
        return { view, error, content };
    };

    t.after(() =>
    {
        for (const view of views)
        {
            view.dispose();
        }
        globalThis.document = oldDocument;
        URL.createObjectURL = oldCreate;
        URL.revokeObjectURL = oldRevoke;
    });
    return { create, elements, revoked,

        /** 試験対象が利用できない応答を作成します。 */
        unsupported()
        {
            supported = false;
        } };
}

/** 試験データを分割して読み取るストリームを返します。
 * @returns arrayBufferを含む結果です。
 */
const stream = (size = 4) => ({
    /** 試験データをバイト領域へ変換します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async arrayBuffer()
    {
        return new ArrayBuffer(size);
    } });

test('image/audio/video use blob media elements, controls and explicit playback', async t =>
{
    const f = fixture(t), v = f.create();
    for (const [type, tag] of [['image/png', 'img'], ['audio/wav', 'audio'], ['video/mp4', 'video']])
    {
        assert.equal(await v.view.load(stream(), type, 'unsupported', 'budget'), null);
        const element = f.elements.at(-1);
        assert.equal(element.tag, tag);
        assert.match(element.src, /^blob:/);
        assert.equal(element.autoplay, undefined);
        if (tag !== 'img')
        {
            assert.equal(element.controls, true);
        }
    }
    assert.equal(f.revoked.length, 2);
    v.view.dispose();
    assert.equal(f.revoked.length, 3);
    assert.equal(f.elements.at(-1).paused, true);
});
test('unsupported formats and codecs never load a source; playback errors are visible', async t =>
{
    const f = fixture(t), v = f.create();
    assert.equal(await v.view.load(stream(), 'text/html', 'unsupported', 'budget'), 'unsupported');
    assert.equal(f.elements.length, 0);
    f.unsupported();
    assert.equal(await v.view.load(stream(), 'audio/ogg', 'unsupported', 'budget'), 'unsupported');
    assert.equal(await v.view.load(stream(), 'image/png', 'unsupported', 'budget'), null);
    f.elements.at(-1).events.error();
    assert.equal(v.error.textContent, 'unsupported');
});
test('disposing during a read does not create an orphan blob or playing element', async t =>
{
    const f = fixture(t), v = f.create();
    let finish;
    const pending = v.view.load({ arrayBuffer: () => new Promise(resolve =>
    {
        finish = resolve;
    }) }, 'image/png', 'unsupported', 'budget');
    v.view.dispose();
    finish(new ArrayBuffer(4));
    await pending;
    assert.equal(v.content.element, undefined);
    assert.equal(f.revoked.length, 0);
});
test('a newer load supersedes an older pending read', async t =>
{
    const f = fixture(t), v = f.create();
    let finish;
    const old = v.view.load({ arrayBuffer: () => new Promise(resolve =>
    {
        finish = resolve;
    }) }, 'image/png', 'unsupported', 'budget');
    await v.view.load(stream(), 'image/jpeg', 'unsupported', 'budget');
    const current = v.content.element;
    finish(new ArrayBuffer(4));
    await old;
    assert.equal(v.content.element, current);
});
test('view count and byte budget are bounded and releasing media restores capacity', async t =>
{
    const f = fixture(t), all = Array.from({ length: 9 }, () => f.create());
    for (const v of all.slice(0, 8))
    {
        assert.equal(await v.view.load(stream(), 'image/png', 'unsupported', 'budget'), null);
    }
    assert.equal(await all[8].view.load(stream(), 'image/png', 'unsupported', 'budget'), 'budget');
    all[0].view.clear();
    assert.equal(await all[8].view.load(stream(), 'image/png', 'unsupported', 'budget'), null);
    for (const v of all)
    {
        v.view.clear();
    }
    for (const v of all.slice(0, 4))
    {
        assert.equal(await v.view.load(stream(16 * 1024 * 1024), 'image/png', 'unsupported', 'budget'), null);
    }
    assert.equal(await all[4].view.load(stream(1), 'image/png', 'unsupported', 'budget'), 'budget');
});
