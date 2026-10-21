import { test } from 'node:test';
import assert from 'node:assert/strict';
import { browserFilesView } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-files.js';
import { LocalFile, LocalFolder } from './fixtures/local-files.mjs';

/** 画面要素の状態とイベントを試験用に再現します。 */
class Element
{
    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(tag)
    {
        this.tag = tag;
        this.children = [];
        this.attributes = {};
        this.value = '';
        this.className = '';
        this.classList = { add: name =>
        {
            const assignedValue = this.className += ' ' + name;
            return assignedValue;
        },

        remove: name =>
        {
            const assignedValue2 = this.className = this.className.split(' ').filter(x => x !== name).join(' ');
            return assignedValue2;
        },

        toggle: (name, on) => on ? this.classList.add(name) : this.classList.remove(name) };
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(...items)
    {
        for (const item of items)
        {
            this.children.push(item);
            item.parent = this;
        }
    }

    /** 子要素を表示順の先頭へ追加します。 */
    prepend(item)
    {
        this.children.unshift(item);
        item.parent = this;
    }

    /** 現在の子要素を指定された内容へ置き換えます。 */
    replaceChildren(...items)
    {
        this.children = [];
        this.text = '';
        this.append(...items);
    }

    /** 指定した子要素の前へ要素を挿入します。 */
    insertBefore(item, before)
    {
        this.children.splice(this.children.indexOf(before), 0, item);
        item.parent = this;
    }

    /** 親との関係を解除して試験用DOMから取り除きます。 */
    remove()
    {
        this.parent.children.splice(this.parent.children.indexOf(this), 1);
    }

    /** 新しい項目を現在の集合へ追加します。 */
    add(item)
    {
        this.append(item);
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute(key, value)
    {
        this.attributes[key] = value;
    }

    /** 保持している選択候補を返します。
     * @returns 呼出し元が使用する値です。
     */
    get options()
    {
        return this.children;
    }

    /** 試験用DOMの文字列内容を読み書きします。
     * @returns 呼出し元が使用する値です。
     */
    get textContent()
    {
        return (this.text ?? '') + this.children.map(item => item.textContent).join('');
    }

    /** 試験用DOMの文字列内容を読み書きします。 */
    set textContent(value)
    {
        this.text = value;
        this.children = [];
    }
}

/** 対象とその子孫を試験用の一覧へ展開します。
 * @returns 呼出し元が使用する値です。
 */
const all = root => [root, ...root.children.flatMap(all)];

const peer = { clusterId: 'cluster', hostId: 'host', peerId: 'peer', instanceId: 'old', status: 'ready' };

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t, { folder = new LocalFolder('local', [new LocalFile('local.txt', 'hello')]), picker = true } = {})
{
    const saved = ['document', 'Option', 'showDirectoryPicker', 'fetch'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]), calls = [], actions = [], notices = [], picks = [];
    const controller = new AbortController();
    let inventory = [peer], fetches = 0, failCommit = false;
    globalThis.document = { createElement: tag => new Element(tag) };
    globalThis.Option = class extends Element
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(text, value)
        {
            super('option');
            this.textContent = text;
            this.value = value;
        }
    };
    if (picker)
    {
        globalThis.showDirectoryPicker = async options =>
        {
            picks.push(options);
            return folder;
        };
    }
    else
    {
        delete globalThis.showDirectoryPicker;
    }
    globalThis.fetch = async() =>
    {
        fetches++;
        return new Response('abc', { headers: { 'content-length': '3' } });
    };
    const root = new Element('main');
    const ctx = { signal: controller.signal,

        t: en => en,

        icon: () => new Element('svg'),

        say: text => notices.push(text),

        peers: async() => inventory,

        /** 試験で要求された処理を実行して結果を保持します。
         * @returns 呼出し元が使用する値です。
         */
        guard(action)
        {
            const promise = Promise.resolve().then(action).catch(error => notices.push(error.message));
            actions.push(promise);
            return promise;
        },

        /** 操作の実行と処理中表示を持つボタンを構成します。
         * @returns 呼出し元が使用する値です。
         */
        button(text, action)
        {
            const button = new Element('button');
            button.textContent = text;
            button.onclick = action;
            return button;
        },

        /** 取消しと応答の確認を伴う管理要求を送信します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async api(path, target, body)
        {
            calls.push({ path, target, body });
            if (path === '/api/files/browse')
            {
                if (body.path == null)
                {
                    return { path: null, parent: null, entries: [{ name: 'Drive', path: 'C:/', isDirectory: true }] };
                }
                return { path: body.path, parent: body.path === 'C:/' ? null : 'C:/', entries: [{ name: 'remote.txt', path: body.path.replace(/\/$/, '') + '/remote.txt', isDirectory: false }, { name: 'sub', path: body.path.replace(/\/$/, '') + '/sub', isDirectory: true }] };
            }
            if (path.endsWith('/manifest'))
            {
                return { sha256: 'hash' };
            }
            if (path.endsWith('/open'))
            {
                return { id: 'stage' };
            }
            if (path.endsWith('/commit') && failCommit)
            {
                throw new Error('lost response');
            }
            return true;
        } };
    const view = await browserFilesView(root, ctx);
    t.after(() =>
    {
        controller.abort();
        view.dispose();
        for (const [name, descriptor] of saved)
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
    const panes = all(root).filter(item => item.className === 'file-pane');

    /** 操作の実行と処理中表示を持つボタンを構成します。
     * @returns 呼出し元が使用する値です。
     */
    const button = (text, within = root) => all(within).find(item => item.tag === 'button' && item.textContent === text);

    /** 待機している項目を順序に従って処理します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const drainAsync = async() =>
    {
        while (actions.length)
        {
            await Promise.all(actions.splice(0));
        }
    };

    /** 試験対象の二回連続した入力を生成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const doubleAsync = async(name, within) =>
    {
        const item = button(name, within);
        item.onclick({ detail: 1 });
        item.onclick({ detail: 2 });
        item.ondblclick();
        await drainAsync();
    };

    /** 試験対象のホストを選択します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const selectHostAsync = async() =>
    {
        const hosts = all(root).filter(item => item.tag === 'select');
        hosts[1].value = 'host';
        await hosts[1].onchange();
        await doubleAsync('Drive', panes[1]);
    };

    return { root, panes, button, double: doubleAsync, drain: drainAsync, selectHost: selectHostAsync, calls, notices, picks, folder,

        setPeers: value =>
        {
            const assignedValue3 = inventory = value;
            return assignedValue3;
        },

        failCommit: () =>
        {
            const assignedValue4 = failCommit = true;
            return assignedValue4;
        },

        get fetches()
        {
            return fetches;
        } };
}

test('unsupported browser keeps two panes and uses selection before folder navigation', async t =>
{
    const f = await fixtureAsync(t, { picker: false });
    assert.equal(f.panes.length, 2);
    assert.equal(f.button('Choose local folder').disabled, true);
    await f.selectHost();
    const before = f.calls.length;
    f.button('sub', f.panes[1]).onclick({ detail: 1 });
    assert.equal(f.calls.length, before);
    await f.double('sub', f.panes[1]);
    assert.equal(f.calls.at(-1).body.path, 'C:/sub');
    assert.ok(all(f.panes[1]).some(item => item.tag === 'a' && item.textContent === 'Download'));
    const chooser = all(f.panes[0]).find(item => item.type === 'file');
    chooser.files = [new File(['abc'], 'pick.txt')];
    chooser.onchange();
    await f.button('Copy selected →').onclick();
    assert.equal(f.calls.find(item => item.path.endsWith('/open')).body.destinationPath, 'C:/sub/pick.txt');
});

test('local folder access is requested only by the explicit picker and enables both directions', async t =>
{
    const f = await fixtureAsync(t);
    assert.equal(f.picks.length, 0);
    assert.equal(f.folder.enumerations, 0);
    await f.button('Choose local folder').onclick();
    assert.deepEqual(f.picks, [{ mode: 'readwrite' }]);
    await f.selectHost();
    f.button('local.txt', f.panes[0]).onclick({ detail: 1 });
    await f.button('Copy selected →').onclick();
    assert.equal(f.calls.filter(item => item.path.endsWith('/commit')).length, 1);
    f.button('remote.txt', f.panes[1]).onclick({ detail: 1 });
    await f.button('← Copy selected').onclick();
    assert.equal(f.fetches, 1);
    assert.equal(await(await f.folder.children.get('remote.txt').getFile()).text(), 'abc');
});

test('denied and revoked permissions never enumerate or write local files', async t =>
{
    const folder = new LocalFolder('denied');
    folder.permission = 'denied';
    const f = await fixtureAsync(t, { folder });
    await assert.rejects(f.button('Choose local folder').onclick(), /not granted/);
    assert.equal(folder.enumerations, 0);
    folder.permission = 'granted';
    await f.button('Choose local folder').onclick();
    await f.selectHost();
    folder.permission = 'denied';
    f.button('remote.txt', f.panes[1]).onclick({ detail: 1 });
    await f.button('← Copy selected').onclick();
    assert.equal(f.fetches, 0);
    assert.equal(folder.children.size, 0);
    assert.match(f.root.textContent, /not granted/);
});

test('dragging selected local files onto a host folder pins that destination', async t =>
{
    const f = await fixtureAsync(t);
    await f.button('Choose local folder').onclick();
    await f.selectHost();
    const row = f.button('local.txt', f.panes[0]).parent;
    row.ondragstart({ dataTransfer: {
        /** ドラッグに対応する試験用データを保持します。 */
        setData()
        {} } });
    f.button('sub', f.panes[1]).parent.ondrop({
        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {},

        /** イベント伝搬を止めたことを記録します。 */
        stopPropagation()
        {} });
    await f.drain();
    assert.equal(f.calls.find(item => item.path.endsWith('/open')).body.destinationPath, 'C:/sub/local.txt');
});

test('a restarted host cannot receive a copy from an old browser selection', async t =>
{
    const f = await fixtureAsync(t);
    await f.button('Choose local folder').onclick();
    await f.selectHost();
    f.setPeers([{ ...peer, instanceId: 'new' }]);
    f.button('local.txt', f.panes[0]).onclick({ detail: 1 });
    await f.button('Copy selected →').onclick();
    assert.ok(!f.calls.some(item => item.path.endsWith('/open')));
    assert.match(f.root.textContent, /Host instance changed/);
});

test('lost commit stops a multi-file batch without replaying the uncertain copy', async t =>
{
    const f = await fixtureAsync(t, { picker: false });
    await f.selectHost();
    const chooser = all(f.panes[0]).find(item => item.type === 'file');
    chooser.files = [new File(['a'], 'one.txt'), new File(['b'], 'two.txt')];
    chooser.onchange();
    f.failCommit();
    await f.button('Copy selected →').onclick();
    assert.equal(f.calls.filter(item => item.path.endsWith('/commit')).length, 1);
    assert.equal(f.calls.filter(item => item.path.endsWith('/open')).length, 1);
    assert.match(f.root.textContent, /Commit unconfirmed/);
    assert.match(f.root.textContent, /Not started/);
});
