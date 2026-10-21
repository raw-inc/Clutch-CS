import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createTerminalStream } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/terminal-stream.js';

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture(t, created = false)
{
    const names = ['WebSocket', 'location', 'setTimeout', 'clearTimeout'], saved = names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]);
    let socket, listener, id = 0;
    const timers = new Map(), writes = [], failures = [];

    /** 接続と送受信を試験で制御できるWebSocketです。 */
    class Socket
    {
        static OPEN = 1;

        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(url)
        {
            this.url = url;
            this.readyState = 1;
            this.bufferedAmount = 0;
            this.sent = [];
            socket = this;
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

        /** 試験で指定した通知を受信側へ渡します。 */
        emit(value)
        {
            this.onmessage?.({ data: JSON.stringify(value) });
        }
    }

    Object.assign(globalThis, { WebSocket: Socket, location: { href: 'https://contents.test/terminal' },

        setTimeout: (callback, delay) =>
        {
            timers.set(++id, { callback, delay });
            return id;
        },

        clearTimeout: key => timers.delete(key) });
    const session = { sessionId: 'session', owner: 'owner', sequence: 0 }, peer = { clusterId: 'cluster', peerId: 'peer', instanceId: 'instance' };
    const term = { options: {}, cols: 100, rows: 30,

        /** 試験用ターミナルの入力通知を登録します。
         * @returns disposeを含む結果です。
         */
        onData(callback)
        {
            listener = callback;
            return {
                /** 所有する監視・接続・表示資源を解放します。 */
                dispose()
                {
                    listener = null;
                } };
        },

        /** 一時状態を初期値へ戻します。 */
        reset()
        {},

        /** 指定された値を所有する保存先へ書き込みます。 */
        write(data, callback)
        {
            writes.push(callback);
            if (data instanceof Uint8Array && new TextDecoder().decode(data).includes('\x1b]10;?') && !this.options.disableStdin)
            {
                listener?.('\x1b]10;rgb:e5e5/e7e7/ebeb\x1b\\');
            }
        } };
    const stream = createTerminalStream(term, peer, session, { created,

        active: () => true,

        /** 処理を開始できる準備状態を確認します。 */
        ready()
        {},

        closed: reason => failures.push(reason),

        problem: reason => failures.push(reason) });

    /** 試験対象の処理を開始し、観測できる状態を返します。 */
    const run = delay =>
    {
        const next = [...timers].find(([,value]) => value.delay === delay);
        assert.ok(next, `timer ${delay}`);
        timers.delete(next[0]);
        next[1].callback();
    };

    /** 処理済みのデータを出力先へ渡します。
     * @returns 呼出し元が使用する値です。
     */
    const output = (text, offset = 0, history = true) => socket.emit({ type: 'output', history, output: { offset, nextOffset: offset + text.length, data: btoa(text), session } });

    /** 処理を開始できる準備状態を確認します。
     * @returns 呼出し元が使用する値です。
     */
    const ready = () => socket.emit({ type: 'ready', session });

    /** 試験用の確認応答を配送します。 */
    const ack = () =>
    {
        const input = socket.sent.filter(x => x.type === 'input').at(-1);
        socket.emit({ type: 'ack', session: { ...session, sequence: input.request.sequence + 1 } });
    };

    t.after(() =>
    {
        stream.close();
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
    return { socket, term, stream, writes, failures, timers, run, output, ready, ack,

        input: data => listener?.(data) };
}

test('replayed color queries cannot be sent back into the shell, and output waits for parsing', t =>
{
    const f = fixture(t);
    f.output('\x1b]10;?\x07');
    f.input('\x1b]11;rgb:1010/1111/1414\x1b\\');
    assert.equal(f.socket.sent.length, 0);
    assert.equal(f.term.options.disableStdin, true);
    f.writes.shift()();
    assert.deepEqual(f.socket.sent.map(x => x.type), ['consumed']);
    f.ready();
    f.run(8);
    assert.equal(atob(f.socket.sent.at(-1).request.data), '');
    f.ack();
    assert.equal(f.timers.size, 0, 'idle sessions send no application polls');
});

test('new shells and live applications still receive terminal capability responses', t =>
{
    const f = fixture(t, true);
    f.output('\x1b]10;?\x07');
    f.writes.shift()();
    f.ready();
    f.run(8);
    assert.match(atob(f.socket.sent.at(-1).request.data), /rgb:e5e5/);
    f.ack();
    f.output('\x1b]10;?\x07', 7, false);
    f.writes.shift()();
    f.run(8);
    assert.match(atob(f.socket.sent.at(-1).request.data), /rgb:e5e5/);
});

test('bursts are coalesced, acknowledgments order input, and equal dimensions send no resize', t =>
{
    const f = fixture(t);
    f.ready();
    f.run(8);
    f.ack();
    f.stream.resize();
    assert.equal(f.timers.size, 0);
    for (let i = 0;i < 100;i++)
    {
        f.input('a');
    }
    f.run(8);
    assert.equal(f.socket.sent.filter(x => x.type === 'input').length, 2);
    assert.equal(atob(f.socket.sent.at(-1).request.data), 'a'.repeat(100));
    f.input('b');
    assert.equal([...f.timers.values()].filter(x => x.delay === 8).length, 0);
    f.ack();
    f.run(8);
    assert.equal(f.socket.sent.at(-1).request.sequence, 2);
    assert.equal(atob(f.socket.sent.at(-1).request.data), 'b');
    f.ack();
    f.term.cols = 120;
    f.stream.resize();
    f.run(8);
    assert.equal(f.socket.sent.at(-1).request.columns, 120);
});

test('unconfirmed input closes without replaying queued keystrokes', t =>
{
    const f = fixture(t);
    f.ready();
    f.run(8);
    f.ack();
    f.input('command');
    f.run(8);
    f.input('next');
    f.run(10000);
    assert.deepEqual(f.failures, ['input-unconfirmed']);
    assert.equal(f.socket.readyState, 3);
    assert.equal(f.timers.size, 0);
    assert.equal(f.socket.sent.filter(x => x.type === 'input').length, 2);
});

test('reviewing an exited shell does not send a resize or enable input', t =>
{
    const f = fixture(t);
    f.socket.emit({ type: 'ready', session: { sessionId: 'session', owner: 'owner', sequence: 0, exited: true } });
    assert.equal(f.stream.ready, false);
    assert.equal(f.term.options.disableStdin, true);
    assert.equal(f.socket.sent.length, 0);
    assert.equal(f.timers.size, 0);
});

test('screen keys use the same acknowledged input sequence and cannot write while unready', t =>
{
    const f = fixture(t);
    f.stream.input('\x03');
    assert.equal(f.socket.sent.length, 0);
    f.ready();
    f.run(8);
    f.ack();
    f.stream.input('\x03');
    f.run(8);
    f.input('a');
    f.stream.input('\x1b[A');
    assert.equal(atob(f.socket.sent.at(-1).request.data), '\x03');
    f.ack();
    f.run(8);
    assert.equal(atob(f.socket.sent.at(-1).request.data), 'a\x1b[A');
    f.stream.close();
    f.stream.input('\r');
    assert.equal(f.socket.sent.filter(item => item.type === 'input').length, 3);
});
