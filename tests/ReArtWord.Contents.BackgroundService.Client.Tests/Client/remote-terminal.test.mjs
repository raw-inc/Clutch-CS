import { test } from 'node:test';
import assert from 'node:assert/strict';
import { terminalView } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-terminal.js';

/** 画面要素の状態とイベントを試験用に再現します。 */
class Element
{
    /** 試験で操作する状態とブラウザー境界を初期化します。 */
    constructor(tag)
    {
        this.tag = tag;
        this.children = [];
        this.dataset = {};
        this.style = {};
        this.className = '';
        this.classList = { toggle: (name, on) =>
        {
            const values = new Set(this.className.split(' ').filter(Boolean));
            if (on)
            {
                values.add(name);
            }
            else
            {
                values.delete(name);
            }
            this.className = [...values].join(' ');
        } };
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(...items)
    {
        this.children.push(...items);
    }

    /** 子要素を表示順の先頭へ追加します。 */
    prepend(...items)
    {
        this.children.unshift(...items);
    }

    /** 現在の子要素を指定された内容へ置き換えます。 */
    replaceChildren(...items)
    {
        this.children = items;
        this.text = '';
    }

    /** 試験用DOMの文字列内容を読み書きします。 */
    set textContent(value)
    {
        this.text = value;
        this.children = [];
    }

    /** 試験用DOMの文字列内容を読み書きします。
     * @returns 呼出し元が使用する値です。
     */
    get textContent()
    {
        return (this.text ?? '') + this.children.map(item => item.textContent).join('');
    }

    /** 条件に一致する試験用DOM要素を列挙します。
     * @returns 呼出し元が使用する値です。
     */
    querySelectorAll(selector)
    {
        return all(this).slice(1).filter(item => item.tag === 'button' && (!selector.includes(':disabled') || !item.disabled));
    }

    /** 条件に一致する試験用DOM要素を取得します。
     * @returns 呼出し元が使用する値です。
     */
    querySelector(selector)
    {
        return this.querySelectorAll(selector)[0];
    }

    /** 属性の値を試験用DOMへ記録します。 */
    setAttribute()
    {}

    /** 試験で呼び出せるイベントハンドラーを登録します。 */
    addEventListener()
    {}

    /** 指定した要素が自分または子孫かを判定します。
     * @returns 呼出し元が使用する値です。
     */
    contains(target)
    {
        return all(this).includes(target);
    }

    /** 操作対象の入力欄へフォーカスを移します。 */
    focus()
    {}
}

/** 対象とその子孫を試験用の一覧へ展開します。
 * @returns 呼出し元が使用する値です。
 */
const all = root => [root, ...root.children.flatMap(all)];

const a = { clusterId: 'cluster-a', hostId: 'host-a', peerId: 'peer-a', instanceId: 'instance-a' };
const b = { clusterId: 'cluster-b', hostId: 'host-b', peerId: 'peer-b', instanceId: 'instance-b' };

/** 試験用のシェル情報を構成します。
 * @returns sessionId・shell・elevated・exited・sequence・ownerを含む結果です。
 */
const shell = id => ({ sessionId: id, shell: 'pwsh.exe', elevated: false, exited: false, sequence: 0, owner: 'owner' });

/** 完了済みのPromiseの継続を、次のイベント処理まで待ちます。
 * @returns 模擬応答を処理した後に完了します。
 */
const turnsAsync = () => new Promise(resolve =>
{
    setImmediate(resolve);
});

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function fixtureAsync(t, custom, experimental = false, alwaysExperimental = false)
{
    const names = ['document', 'window', 'ResizeObserver', 'setTimeout', 'clearTimeout', 'requestAnimationFrame', 'cancelAnimationFrame', 'location', 'localStorage', 'WebSocket'];
    const saved = names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]);
    Object.defineProperty(globalThis, 'localStorage', { value: { getItem: key => key === 'contents.ui.experimental' && alwaysExperimental ? 'true' : null }, configurable: true });
    const timers = new Map(), calls = [], terms = [], notices = [], sockets = [], controller = new AbortController();
    let id = 0;
    const values = new Map([['peer-a', [shell('shared-id')]], ['peer-b', [shell('shared-id')]]]);
    const failed = new Set();
    let peers = [a, b], claimed, shellRead, capabilityRead, settingsSave;
    Object.assign(globalThis, { document: { createElement: tag => new Element(tag),

        /** 試験で呼び出せるイベントハンドラーを登録します。 */
        addEventListener()
        {},

        querySelector: () => null },
    window: {
        /** 試験で呼び出せるイベントハンドラーを登録します。 */
        addEventListener()
        {},

        Terminal: class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor(options)
            {
                this.options = options;
                this.cols = 80;
                this.rows = 24;
                this.disposed = false;
                terms.push(this);
            }

            /** 試験用ターミナルへ拡張機能を登録します。 */
            loadAddon()
            {}

            /** 指定した対象を開き、表示状態を更新します。 */
            open()
            {}

            /** 操作対象の入力欄へフォーカスを移します。 */
            focus()
            {}

            /** 試験用ターミナルの入力通知を登録します。 */
            onData(value)
            {
                this.data = value;
            }

            /** 試験用ターミナルのキー変換を登録します。 */
            attachCustomKeyEventHandler()
            {}

            /** 試験用ターミナルの寸法変更通知を登録します。 */
            onResize()
            {}

            /** 指定された値を所有する保存先へ書き込みます。 */
            write()
            {}

            /** 所有する監視・接続・表示資源を解放します。 */
            dispose()
            {
                this.disposed = true;
            }
        }, FitAddon: { FitAddon: class
        {
            /** 試験用ターミナルの表示寸法を調整します。 */
            fit()
            {}
        } } },
    location: { href: 'https://contents.test/terminal', search: experimental ? '?experimental=true' : '' }, WebSocket: class
    {
        static OPEN = 1;

        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor()
        {
            this.readyState = 1;
            this.bufferedAmount = 0;
            this.sent = [];
            sockets.push(this);
        }

        /** 固定された通信形式で入力を送信します。 */
        send(value)
        {
            this.sent.push(JSON.parse(value));
        }

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            this.readyState = 3;
        }
    },

    requestAnimationFrame: callback =>
    {
        timers.set(++id, { callback, delay: 16 });
        return id;
    },

    cancelAnimationFrame: key => timers.delete(key),

    ResizeObserver: class
    {
        /** 試験対象の要素を監視対象として記録します。 */
        observe()
        {}

        /** 接続と描画を終了し、保留中の解放処理を保持します。 */
        disconnect()
        {}
    },

    setTimeout: (callback, delay) =>
    {
        timers.set(++id, { callback, delay });
        return id;
    },

    clearTimeout: key => timers.delete(key) });
    const root = new Element('main');
    const ctx = { signal: controller.signal,

        t: en => en,

        say: text => notices.push(text),

        icon: () => new Element('svg'),

        guard: action => action(),

        peers: async() => peers,

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
        async api(path, target, body, signal)
        {
            calls.push({ path, target, body, signal, terminalDisposed: terms.at(-1)?.disposed });
            if (custom && path.endsWith('/sessions'))
            {
                return custom(peers, signal ?? controller.signal);
            }
            if (path.endsWith('/sessions'))
            {
                return peers.map(peer => ({ clusterId: peer.clusterId, peer, sessions: values.get(peer.peerId) ?? [], confirmed: !failed.has(peer.peerId) }));
            }
            if (path.endsWith('/shells'))
            {
                return shellRead ? await shellRead(target) : [{ name: 'pwsh', path: 'C:/pwsh.exe' }, { name: 'cmd', path: 'C:/cmd.exe' }];
            }
            if (path.endsWith('/capabilities'))
            {
                return capabilityRead ? await capabilityRead(target) : { userAvailable: true, administratorAvailable: true };
            }
            if (path.endsWith('/settings/save'))
            {
                return settingsSave?.(target, body);
            }
            if (path.endsWith('/create'))
            {
                claimed = { ...shell(body.sessionId), shell: body.shell, owner: body.owner };
                values.get(target.peerId).push(claimed);
                return claimed;
            }
            if (path.endsWith('/claim'))
            {
                claimed = { ...shell(body.sessionId), owner: body.owner };
                return claimed;
            }
            if (path.endsWith('/input'))
            {
                return { sequence: body.sequence + 1 };
            }
            if (path.endsWith('/read'))
            {
                return { session: claimed, offset: 0, nextOffset: 0, data: '' };
            }
            if (path.endsWith('/close'))
            {
                values.set(target.peerId, []);
                return true;
            }
        } };
    let view;
    const mounting = terminalView(root, ctx).then(value =>
    {
        const assignedValue = view = value;
        return assignedValue;
    });
    t.after(async() =>
    {
        controller.abort();
        await mounting;
        view?.dispose();
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

    /** 経過時間に応じて状態と期限を更新します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const tickAsync = async() =>
    {
        const entry = [...timers].find(([,value]) => value.delay === 3000);
        assert.ok(entry);
        timers.delete(entry[0]);
        entry[1].callback();
        await turnsAsync();
    };

    /** 操作の実行と処理中表示を持つボタンを構成します。
     * @returns 呼出し元が使用する値です。
     */
    const button = text => all(root).find(item => item.tag === 'button' && item.textContent === text);

    /** 表示の検証に使用する一覧行を作成します。
     * @returns 呼出し元が使用する値です。
     */
    const row = peer => all(root).find(item => item.className.split(' ').includes('terminal-session') && item.dataset.peer.includes(peer.peerId));

    return { root, calls, values, failed, terms, timers, sockets,

        get claimed()
        {
            return claimed;
        },

        mounting, tick: tickAsync, button, row, controller,

        setPeers: items =>
        {
            const assignedValue2 = peers = items;
            return assignedValue2;
        },

        setShellRead: value =>
        {
            const assignedValue3 = shellRead = value;
            return assignedValue3;
        },

        setCapabilityRead: value =>
        {
            const assignedValue4 = capabilityRead = value;
            return assignedValue4;
        },

        setSettingsSave: value =>
        {
            const assignedValue5 = settingsSave = value;
            return assignedValue5;
        },

        get view()
        {
            return view;
        } };
}

test('experimental token policy is saved on the chosen Core and refreshed without recreating a shell', async t =>
{
    const f = await fixtureAsync(t, undefined, true);
    await f.mounting;
    let settings = { strictUserToken: false, version: 'one' };
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: true, settings }));
    f.setSettingsSave((target, body) =>
    {
        assert.equal(target.peerId, a.peerId);
        assert.deepEqual(body, { strictUserToken: true, version: 'one' });
        settings = { strictUserToken: true, version: 'two' };
        return settings;
    });
    await f.button('host-a / peer-a').onclick();
    await f.row(a).children.find(item => item.textContent === 'Take over').onclick();
    await f.button('Strict unelevated: OFF').onclick();
    assert(f.button('Strict unelevated: ON'));
    assert.equal(f.terms[0].disposed, false);
    assert.equal(f.calls.filter(c => c.path.endsWith('/create')).length, 0);
});

test('browser preference enables the token policy without a URL opt-in', async t =>
{
    const f = await fixtureAsync(t, undefined, false, true);
    await f.mounting;
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: true, settings: { strictUserToken: false, version: 'one' } }));
    await f.button('host-a / peer-a').onclick();
    assert(f.button('Strict unelevated: OFF'));
    assert.equal(f.calls.filter(call => call.path.endsWith('/settings') && call.body).length, 0);
});

test('a failed token policy save reloads actual Core state without claiming the optimistic value', async t =>
{
    const f = await fixtureAsync(t, undefined, true);
    await f.mounting;
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: true, settings: { strictUserToken: false, version: 'one' } }));
    f.setSettingsSave(() =>
    {
        throw new Error('not confirmed');
    });
    await f.button('host-a / peer-a').onclick();
    await assert.rejects(f.button('Strict unelevated: OFF').onclick(), /not confirmed/);
    assert(f.button('Strict unelevated: OFF'));
    assert.equal(f.button('Strict unelevated: OFF').disabled, false);
});

test('the persistent Core policy has no editing control outside experimental mode', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: true, settings: { strictUserToken: true, version: 'two' } }));
    await f.button('host-a / peer-a').onclick();
    assert.equal(f.button('Strict unelevated: ON'), undefined);
    await f.button('＋ User session').onclick();
    assert.equal(f.calls.find(c => c.path.endsWith('/create')).body.strictUserToken, undefined);
});

test('administrator mode stays disabled without a confirmed elevated user token and follows refreshed capabilities', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: false, administratorReason: 'CoreAdministratorRequired' }));
    await f.button('host-a / peer-a').onclick();
    assert.equal(f.button('＋ Administrator').disabled, true);
    assert.equal(f.button('＋ User session').disabled, false);
    await f.button('＋ Administrator').onclick();
    assert.equal(f.calls.filter(c => c.path.endsWith('/create')).length, 0);
    f.setCapabilityRead(async() => ({ userAvailable: true, administratorAvailable: true }));
    await f.tick();
    assert.equal(f.button('＋ Administrator').disabled, false);
    f.setCapabilityRead(async() =>
    {
        throw Error('unavailable');
    });
    await f.tick();
    assert.equal(f.button('＋ Administrator').disabled, true);
});

test('no logged-on user makes SYSTEM explicit and disables ordinary user creation', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    f.setCapabilityRead(async() => ({ userAvailable: false, administratorAvailable: true, administratorUsesSystem: true }));
    await f.button('host-a / peer-a').onclick();
    assert.equal(f.button('＋ User session').disabled, true);
    assert.equal(f.button('＋ SYSTEM session').disabled, false);
    assert.match(f.button('＋ SYSTEM session').title, /No user is logged on/);
    f.values.set('peer-a', [{ ...shell('system-session'), elevated: true, system: true }]);
    await f.tick();
    assert.match(f.row(a).textContent, /SYSTEM/);
});

test('shell catalog is cached by Core instance and the chosen shell is pinned on creation', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    await f.button('host-a / peer-a').onclick();
    const choice = all(f.root).find(item => item.tag === 'select');
    assert.equal(choice.value, 'C:/pwsh.exe');
    choice.value = 'C:/cmd.exe';
    choice.onchange();
    await f.tick();
    await f.tick();
    await f.button('host-b / peer-b').onclick();
    await f.button('host-a / peer-a').onclick();
    assert.equal(choice.value, 'C:/cmd.exe');
    assert.equal(f.calls.filter(call => call.path.endsWith('/shells') && call.target.peerId === 'peer-a').length, 1);
    await f.button('＋ User session').onclick();
    const created = f.calls.find(call => call.path.endsWith('/create'));
    assert.equal(created.body.shell, 'C:/cmd.exe');
    assert.equal(created.target.instanceId, a.instanceId);
    choice.value = 'C:/pwsh.exe';
    choice.onchange();
    assert.equal(f.claimed.shell, 'C:/cmd.exe');
});

test('late shell responses cannot replace the newly selected peer catalog', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    let release;
    f.setShellRead(target => target.peerId === a.peerId ? new Promise(resolve =>
    {
        release = resolve;
    }) : [{ name: 'other', path: 'C:/other.exe' }]);
    const first = f.button('host-a / peer-a').onclick();
    await turnsAsync();
    assert.equal(f.button('＋ User session').disabled, true);
    await f.button('host-b / peer-b').onclick();
    release([{ name: 'stale', path: 'C:/stale.exe' }]);
    await first;
    assert.equal(all(f.root).find(item => item.tag === 'select').value, 'C:/other.exe');
});

test('missing shell metadata disables creation without breaking session takeover or polling repeatedly', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    f.setShellRead(() =>
    {
        throw new Error('unavailable');
    });
    await f.button('host-a / peer-a').onclick();
    assert.equal(f.button('＋ User session').disabled, true);
    assert.equal(f.button('＋ Administrator').disabled, true);
    await f.tick();
    assert.equal(f.calls.filter(call => call.path.endsWith('/shells')).length, 1);
    await f.row(a).children.find(item => item.textContent === 'Take over').onclick();
    assert.equal(f.terms.length, 1);
    await f.button('host-a / peer-a').onclick();
    assert.equal(f.calls.filter(call => call.path.endsWith('/shells')).length, 2);
});

test('a new Core instance receives a fresh catalog instead of the previous instance choice', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    await f.button('host-a / peer-a').onclick();
    const choice = all(f.root).find(item => item.tag === 'select');
    choice.value = 'C:/cmd.exe';
    choice.onchange();
    f.setPeers([{ ...a, instanceId: 'restarted' }, b]);
    f.setShellRead(() => [{ name: 'new', path: 'C:/new.exe' }]);
    await f.tick();
    await f.button('host-a / peer-a').onclick();
    assert.equal(choice.value, 'C:/new.exe');
    assert.equal(f.calls.filter(call => call.path.endsWith('/shells')).at(-1).target.instanceId, 'restarted');
});

test('all shared sessions are visible without selecting a peer and selection does not filter them', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    assert.ok(f.row(a));
    assert.ok(f.row(b));
    assert.equal(f.button('＋ User session').disabled, true);
    assert.equal(f.button('Refresh sessions').disabled, false);
    await f.button('host-b / peer-b').onclick();
    assert.ok(f.row(a));
    assert.ok(f.row(b));
    assert.equal(f.calls.filter(call => call.path.endsWith('/claim')).length, 0);
});

test('takeover and termination route to the row owner even when another peer is selected', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    await f.button('host-b / peer-b').onclick();
    await f.row(a).children.find(item => item.textContent === 'Take over').onclick();
    assert.equal(f.calls.find(call => call.path.endsWith('/claim')).target.peerId, 'peer-a');
    assert.equal(f.terms.length, 1);
    assert.equal(f.terms[0].disposed, false);
    await f.row(b).children.find(item => item.textContent === 'Terminate').onclick();
    assert.equal(f.calls.find(call => call.path.endsWith('/close')).target.peerId, 'peer-b');
    assert.equal(f.terms[0].disposed, false, 'a duplicate session ID on another peer must not disconnect this terminal');
    await f.tick();
    assert.equal(f.terms.length, 1);
    assert.equal(f.terms[0].disposed, false);
});

test('automatic refresh discovers changes and preserves unavailable peers as unconfirmed', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    const original = f.row(a);
    await f.tick();
    assert.equal(f.row(a), original, 'unchanged polls keep the same interactive DOM');
    f.failed.add('peer-b');
    f.values.set('peer-a', [shell('new-shell')]);
    await f.tick();
    assert.match(f.row(a).textContent, /new-shel/);
    assert.ok(f.row(b));
    assert.equal(f.row(b).children.find(item => item.tag === 'button').disabled, true);
    assert.match(f.root.textContent, /Reads unconfirmed: 1/);
    f.failed.clear();
    f.values.set('peer-b', []);
    await f.tick();
    assert.equal(f.row(b), undefined);
    f.view.dispose();
    assert.equal([...f.timers.values()].filter(value => value.delay === 3000).length, 0);
});

test('failed reads are isolated and disposal aborts pending requests without scheduling another poll', async t =>
{
    const f = await fixtureAsync(t, (_target, signal) => new Promise((resolve, reject) =>
    {
        signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
    }));
    await turnsAsync();
    assert.equal(f.calls.length, 1);
    f.controller.abort();
    await f.mounting;
    f.view.dispose();
    assert.equal(f.timers.size, 0);
});

test('many peers use one aggregate request and overlapping refreshes share that request', async t =>
{
    let hold = false, release;
    const f = await fixtureAsync(t, async peers =>
    {
        if (hold)
        {
            await new Promise(resolve =>
            {
                release = resolve;
            });
        }
        return peers.map(peer => ({ clusterId: peer.clusterId, peer, sessions: [shell(peer.peerId)], confirmed: true }));
    });
    await f.mounting;
    const discovered = Array.from({ length: 9 }, (_, i) => ({ ...a, peerId: 'peer-' + i, instanceId: 'instance-' + i }));
    f.setPeers(discovered);
    hold = true;
    const before = f.calls.length;
    await f.tick();
    const manual = f.button('Refresh sessions').onclick();
    await turnsAsync();
    assert.equal(f.calls.length, before + 1);
    release();
    await manual;
    for (const target of discovered)
    {
        assert.ok(f.row(target));
    }
    assert.equal(f.row(b), undefined);
});

test('explicit termination closes the local stream before the server can report a disconnect', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    await f.row(a).children.find(item => item.textContent === 'Take over').onclick();
    await f.row(a).children.find(item => item.textContent === 'Terminate').onclick();
    assert.equal(f.calls.find(item => item.path.endsWith('/close')).terminalDisposed, true);
    assert.equal(f.terms[0].disposed, true);
});

test('mobile modifier toggles and direct keys reach the connected stream in order', async t =>
{
    const f = await fixtureAsync(t);
    await f.mounting;
    assert.equal(f.button('Ctrl').disabled, true);
    await f.row(a).children.find(item => item.textContent === 'Take over').onclick();
    const socket = f.sockets[0];
    socket.onmessage({ data: JSON.stringify({ type: 'ready', session: f.claimed }) });
    assert.equal(f.button('Ctrl').disabled, false);
    f.button('Ctrl').onclick();
    f.terms[0].data('c');
    f.button('←').onclick();
    const pending = [...f.timers].find(([,value]) => value.delay === 8);
    pending[1].callback();
    assert.equal(atob(socket.sent.at(-1).request.data), '\x03\x1b[1;5D');
    f.button('Ctrl').onclick();
    f.button('Alt').onclick();
    f.terms[0].data('x');
    socket.onmessage({ data: JSON.stringify({ type: 'ack', session: { ...f.claimed, sequence: 1 } }) });
    const next = [...f.timers].filter(([,value]) => value.delay === 8).at(-1);
    next[1].callback();
    assert.equal(atob(socket.sent.at(-1).request.data), '\x1bx');
    await f.button('Disconnect').onclick();
    assert.equal(f.button('Alt').disabled, true);
});
