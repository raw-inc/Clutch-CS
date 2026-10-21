import { test } from 'node:test';
import assert from 'node:assert/strict';
import { filesView } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-files.js';

/** 画面要素の状態とイベントを試験用に再現します。 */
class Element
{
    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(tag)
    {
        this.tag = tag;
        this.children = [];
        this.value = '';
        const classes = new Set();
        this.classList = { add: name => classes.add(name),

            remove: name => classes.delete(name),

            contains: name => classes.has(name),

            toggle: (name, selected) => selected ? classes.add(name) : classes.delete(name) };
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

    /** 現在の子要素を指定された内容へ置き換えます。 */
    replaceChildren(...items)
    {
        this.children = [];
        this.append(...items);
    }

    /** 指定した子要素の前へ要素を挿入します。 */
    insertBefore(item, before)
    {
        this.children.splice(this.children.indexOf(before), 0, item);
        item.parent = this;
    }

    /** 新しい項目を現在の集合へ追加します。 */
    add(item)
    {
        this.append(item);
    }

    /** 子要素を表示順の先頭へ追加します。 */
    prepend(item)
    {
        this.children.unshift(item);
        item.parent = this;
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute()
    {}

    /** 親との関係を解除して試験用DOMから取り除きます。 */
    remove()
    {
        this.parent.children.splice(this.parent.children.indexOf(this), 1);
    }
}

/** 自分と配下の試験用要素を列挙します。
 * @returns 呼出し元が使用する値です。
 */
const descendants = element => [element, ...element.children.flatMap(descendants)];

/** 経過時間に応じて状態と期限を更新します。
 * @returns 呼出し元が使用する値です。
 */
const tick = () => new Promise(resolve =>
{
    setImmediate(resolve);
});

/** ダブルクリックに対応する画面操作を通知します。 */
const doubleClick = button =>
{
    button.onclick({ detail: 1 });
    button.onclick({ detail: 2 });
    button.ondblclick();
};

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t, inventory = null, browse = true)
{
    const originalDocument = globalThis.document, originalOption = globalThis.Option;
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
    const timers = [];
    t.mock.method(globalThis, 'setTimeout', fn =>
    {
        timers.push(fn);
        return timers.length;
    });
    t.mock.method(globalThis, 'clearTimeout', () =>
    {});
    const root = new Element('main'), actions = [], calls = [];
    const peer = { clusterId: 'cluster', peerId: 'peer', instanceId: 'instance', hostId: 'host', status: 'ready' };
    inventory ??= [peer];
    const targets = [], transfers = [], messages = [];
    let jobs = [], copied = false;
    const context = {
        t: en => en,

        say: text => messages.push(text),

        icon: () => new Element('svg'),

        peers: async () => inventory,

        /** 試験で要求された処理を実行して結果を保持します。
         * @returns 呼出し元が使用する値です。
         */
        guard(action)
        {
            const result = action();
            actions.push(result);
            return result;
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
            if (path.startsWith('/client/transfers/state'))
            {
                return { offset: 0, jobs, totalJobs: jobs.length };
            }
            if (path === '/client/transfers')
            {
                transfers.push(body);
                return {};
            }
            assert.equal(path, '/api/files/browse');
            assert.equal(body.fromDrives, true);
            calls.push(body.path);
            targets.push(target);
            if (!body.path)
            {
                return { path: null, parent: null, entries: [{ name: 'Drive', path: '/drive', isDirectory: true }] };
            }
            if (body.path === '/drive')
            {
                return { path: body.path, parent: null, entries: [{ name: 'dest', path: '/drive/dest', isDirectory: true }] };
            }
            const entries = [{ name: 'existing.txt', path: body.path + '/existing.txt', isDirectory: false }];
            if (copied)
            {
                entries.push({ name: 'copied.txt', path: body.path + '/copied.txt', isDirectory: false });
            }
            return { path: body.path, parent: '/drive', entries };
        }
    };
    const cleanup = { view: null };
    t.after(() =>
    {
        cleanup.view?.dispose();
        globalThis.document = originalDocument;
        globalThis.Option = originalOption;
    });
    cleanup.view = await filesView(root, context);
    const hosts = descendants(root).filter(e => e.tag === 'select');
    if (browse)
    {
        hosts[2].value = 'host';
        await hosts[2].onchange();
    }
    await tick();
    const pane = descendants(root).filter(e => e.className === 'file-pane')[1];

    /** クリック対象を確認して選択または操作を行います。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const clickAsync = async name =>
    {
        doubleClick(descendants(pane).find(e => e.tag === 'button' && e.textContent === name));
        await Promise.all(actions.splice(0));
        await tick();
    };

    if (browse)
    {
        await clickAsync('Drive');
        await clickAsync('dest');
    }
    return {
        pane, root, calls, hosts, targets, transfers, messages,

        /** 試験に使用するピア一覧を差し替えます。 */
        setPeers(value)
        {
            inventory = value;
        },

        /** 対象と選択状態を照合して切り替えます。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async select(index, value)
        {
            hosts[index].value = value;
            await hosts[index].onchange();
            await tick();
        },

        /** 試験対象の領域へクリックを送ります。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async clickIn(index, text, twice = false)
        {
            const target = descendants(root).filter(e => e.className === 'file-pane')[index];
            const button = descendants(target).find(e => e.tag === 'button' && e.textContent === text);
            if (twice)
            {
                doubleClick(button);
            }
            else
            {
                button.onclick({ detail: 1 });
            }
            await Promise.all(actions.splice(0));
            await tick();
        },

        /** 保留している試験用の要求を完了させます。 */
        complete()
        {
            copied = true;
            jobs = [{ state: 'Succeeded', bytesCopied: 10, totalBytes: 10, request: { id: 'copy', source: { peerId: 'source' }, sourcePath: '/source/copied.txt', destination: peer, destinationDirectory: '/drive/dest' } }];
        },

        /** 処理結果の照会を画面の寿命中だけ繰り返します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async poll()
        {
            assert.ok(timers.length);
            await timers.shift()();
        },

        names: () => descendants(pane).filter(e => e.className === 'file-name').map(e => e.textContent)
    };
}

test('completion refreshes only the open destination once, without a manual refresh', async t =>
{
    const f = await fixtureAsync(t), before = f.calls.length;
    f.complete();
    await f.poll();
    assert.ok(f.names().includes('copied.txt'));
    assert.equal(f.calls.length, before + 1);
    assert.equal(f.calls.at(-1), '/drive/dest');
    await f.poll();
    assert.equal(f.calls.length, before + 1);
});

test('completion waits for selection to finish and then refreshes the pending destination', async t =>
{
    const f = await fixtureAsync(t), before = f.calls.length;
    const check = descendants(f.pane).find(e => e.tag === 'input' && e.type === 'checkbox');
    check.checked = true;
    check.onchange();
    f.complete();
    await f.poll();
    assert.equal(f.calls.length, before);
    assert.equal(check.checked, true);
    check.checked = false;
    check.onchange();
    await f.poll();
    assert.ok(f.names().includes('copied.txt'));
    assert.equal(f.calls.length, before + 1);
});

test('cluster selector precedes distinct host choices and both panes use their common cluster', async t =>
{
    /** 試験に必要なピアの識別情報を作成します。
     * @returns clusterId・hostId・peerId・instanceId・statusを含む結果です。
     */
    const peer = (clusterId, hostId, peerId) => ({ clusterId, hostId, peerId, instanceId: peerId, status: 'ready' });

    const peers = [peer('a', 'left', 'a-left'), peer('b', 'left', 'b-left'), peer('b', 'right', 'b-right'), peer('b', 'right', 'b-extra')];
    const f = await fixtureAsync(t, peers, false);
    assert.equal(f.hosts.length, 3);
    assert.deepEqual(f.hosts[1].children.map(e => e.value), ['', 'left', 'right']);
    await f.select(1, 'left');
    assert.equal(f.targets.at(-1).clusterId, 'a');
    await f.select(2, 'right');
    assert.deepEqual(f.targets.slice(-2).map(target => target.clusterId), ['b', 'b']);
    await f.select(0, 'a');
    assert.equal(f.messages.length, 0);
    assert.equal(f.targets.length, 3);
    assert.ok(descendants(f.root).some(e => e.textContent?.startsWith('No available common cluster')));
});

test('a restarted endpoint cannot enqueue from paths browsed on its old instance', async t =>
{
    const left = { clusterId: 'cluster', hostId: 'left', peerId: 'left-peer', instanceId: 'left-instance', status: 'ready' };
    const right = { ...left, hostId: 'right', peerId: 'right-peer', instanceId: 'right-instance' };
    const f = await fixtureAsync(t, [left, right], false);
    await f.select(1, 'left');
    await f.select(2, 'right');
    await f.clickIn(0, 'Drive', true);
    await f.clickIn(0, 'dest', true);
    await f.clickIn(1, 'Drive', true);
    await f.clickIn(1, 'dest', true);
    const leftPane = descendants(f.root).filter(e => e.className === 'file-pane')[0];
    const check = descendants(leftPane).find(e => e.tag === 'input' && e.type === 'checkbox');
    check.checked = true;
    check.onchange();
    f.setPeers([{ ...left, instanceId: 'restarted' }, right]);
    await f.clickIn(0, 'Copy selected →');
    assert.equal(f.transfers.length, 0);
    assert.ok(f.messages.at(-1).includes('Refresh hosts'));
});

test('copy enqueue keeps browsed peers even when a higher priority peer appears', async t =>
{
    const left = { clusterId: 'cluster', hostId: 'left', peerId: 'left-peer', instanceId: 'left-instance', status: 'ready' };
    const right = { ...left, hostId: 'right', peerId: 'right-peer', instanceId: 'right-instance' };
    const f = await fixtureAsync(t, [left, right], false);
    await f.select(1, 'left');
    await f.select(2, 'right');
    await f.clickIn(0, 'Drive', true);
    await f.clickIn(0, 'dest', true);
    await f.clickIn(1, 'Drive', true);
    await f.clickIn(1, 'dest', true);
    const leftPane = descendants(f.root).filter(e => e.className === 'file-pane')[0];
    const check = descendants(leftPane).find(e => e.tag === 'input' && e.type === 'checkbox');
    check.checked = true;
    check.onchange();
    f.setPeers([left, right, { ...left, peerId: 'new-peer', instanceId: 'new-instance', priority: 100 }]);
    await f.clickIn(0, 'Copy selected →');
    assert.equal(f.transfers.length, 1);
    assert.equal(f.transfers[0].source.peerId, left.peerId);
    assert.equal(f.transfers[0].destination.peerId, right.peerId);
    assert.equal(f.transfers[0].source.clusterId, f.transfers[0].destination.clusterId);
    assert.equal(f.transfers[0].sourcePath, '/drive/dest/existing.txt');
});

test('single clicks select drives and folders; double clicks navigate exactly once', async t =>
{
    const f = await fixtureAsync(t, null, false);
    await f.select(2, 'host');
    const before = f.calls.length;
    await f.clickIn(1, 'Drive');
    assert.equal(f.calls.length, before);
    assert.ok(descendants(f.pane).find(e => e.className === 'file-row').classList.contains('selected'));
    assert.equal(descendants(f.pane).find(e => e.type === 'checkbox').disabled, true);
    await f.clickIn(1, 'Drive', true);
    assert.equal(f.calls.length, before + 1);
    assert.equal(f.calls.at(-1), '/drive');
    await f.clickIn(1, 'dest');
    assert.equal(f.calls.length, before + 1);
    const check = descendants(f.pane).find(e => e.type === 'checkbox');
    assert.equal(check.checked, true);
    assert.ok(descendants(f.pane).some(e => e.textContent === '1 selected'));
    check.checked = false;
    check.onchange();
    assert.ok(descendants(f.pane).some(e => e.textContent === '0 selected'));
    assert.equal(f.calls.length, before + 1);
    await f.clickIn(1, 'dest', true);
    assert.equal(f.calls.length, before + 2);
    assert.equal(f.calls.at(-1), '/drive/dest');
    assert.ok(descendants(f.pane).some(e => e.textContent === '0 selected'));
});

test('double clicking a file selects it once without opening a directory', async t =>
{
    const f = await fixtureAsync(t), before = f.calls.length;
    await f.clickIn(1, 'existing.txt', true);
    assert.equal(f.calls.length, before);
    assert.equal(descendants(f.pane).find(e => e.type === 'checkbox').checked, true);
    await f.clickIn(1, 'existing.txt');
    assert.equal(descendants(f.pane).find(e => e.type === 'checkbox').checked, false);
    assert.equal(f.calls.length, before);
});

test('Enter opens a focused directory while keyboard button activation selects a file', async t =>
{
    const f = await fixtureAsync(t, null, false);
    await f.select(2, 'host');
    await f.clickIn(1, 'Drive', true);
    const folder = descendants(f.pane).find(e => e.className === 'file-name');
    let prevented = false;
    folder.onkeydown({ key: 'Enter',

        /** 既定動作を取り消したことを記録します。 */
        preventDefault()
        {
            prevented = true;
        } });
    await tick();
    assert.equal(prevented, true);
    assert.equal(f.calls.at(-1), '/drive/dest');
    const before = f.calls.length;
    const file = descendants(f.pane).find(e => e.className === 'file-name');
    file.onclick({ detail: 0 });
    assert.equal(descendants(f.pane).find(e => e.type === 'checkbox').checked, true);
    assert.equal(f.calls.length, before);
});
