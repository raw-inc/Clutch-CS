import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopClipboard, desktopClipboardRequest, readDesktopClipboard, readDesktopClipboardTextAsync } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-clipboard.js';

/** 一回の通知を実プロトコルの本文として構築します。
 * @returns 読み取るバイト列です。
 */
function frame(mode, id, version, text = '')
{
    const body = new TextEncoder().encode(text), bytes = new Uint8Array(12 + body.length), header = new DataView(bytes.buffer);
    header.setInt32(0, mode);
    header.setInt32(4, id);
    header.setUint32(8, version);
    bytes.set(body, 12);
    return bytes;
}

/** Promiseで渡されるブラウザーと通信の結果を一巡待ちます。
 * @returns microtaskの処理後に完了します。
 */
function tickAsync()
{
    return new Promise(resolve =>
    {
        setImmediate(resolve);
    });
}

/** OSのクリップボードを触らず、反映通知を手動確認する境界です。
 * @returns 操作と観測された入出力です。
 */
function fixture()
{
    const state = { value: 'client', reads: 0, writes: [], packets: [], controls: [], input: [], pastes: 0, released: 0, errors: [], order: [] };
    const options = {

        read: async () =>
        {
            state.reads++;
            return state.value;
        },

        write: async value =>
        {
            state.value = value;
            state.writes.push(value);
        },

        packet: bytes =>
        {
            state.packets.push(bytes);
            return true;
        },

        control: value => state.controls.push(value),

        input: (...values) =>
        {
            state.input.push(values);
            state.order.push('input');
        },

        paste: () =>
        {
            state.pastes++;
            state.order.push('paste');
        },

        release: () => state.released++,

        error: value => state.errors.push(value)
    };
    const controller = createDesktopClipboard(options);

    /** 元要求だけへ反映確認を返します。 */
    function acknowledge(version = 1, mode = 1)
    {
        const packet = state.packets.at(-1);
        controller.receive(frame(mode, new DataView(packet).getInt32(4), version, mode === 2 ? 'Clipboard.Busy' : ''));
    }

    /** 有効化時の一回のクライアント優先同期を完了します。
     * @returns 初期反映の確認です。
     */
    async function enableAsync()
    {
        controller.connect(true);
        const enabling = controller.enableAsync(true);
        await tickAsync();
        acknowledge();
        await enabling;
    }

    return { state, options, controller, acknowledge, enableAsync };
}

test('default OFF never reads or writes the clipboard, and native text boundaries stay finite', async () =>
{
    const f = fixture();
    f.controller.connect(true);
    await f.controller.activateAsync();
    f.controller.receive(frame(0, 0, 1, 'host'));
    assert.equal(f.controller.enabled, false);
    assert.equal(f.state.reads, 0);
    assert.deepEqual(f.state.writes, []);
    assert.deepEqual(f.state.packets, []);
    const text = '日本語\r\n🌐', encoded = desktopClipboardRequest(7, text);
    assert.equal(new DataView(encoded).getInt32(8), new TextEncoder().encode(text).length);
    assert.equal(new TextDecoder().decode(new Uint8Array(encoded, 24)), text);
    assert.equal(desktopClipboardRequest(1, '').byteLength, 24);
    assert.equal(desktopClipboardRequest(1, 'x'.repeat(65536)).byteLength, 65560);
    for (const value of ['x'.repeat(65537), '🌐'.repeat(16385), 'a\0b', '\ud800'])
    {
        assert.throws(() => desktopClipboardRequest(1, value));
    }
    assert.throws(() => desktopClipboardRequest(0, 'text'));
    assert.throws(() => readDesktopClipboard(new Uint8Array(11)));
    assert.throws(() => readDesktopClipboard(frame(1, 0, 1)));
    const invalid = frame(0, 0, 1, 'x');
    invalid[12] = 255;
    assert.throws(() => readDesktopClipboard(invalid));
});

test('preselected synchronization subscribes on connection, waits for entry and preserves a manual OFF across reconnects', async () =>
{
    const f = fixture();
    await f.controller.enableAsync(true);
    assert.equal(f.controller.enabled, true);
    assert.equal(f.controller.available, false);
    await f.controller.activateAsync();
    assert.equal(f.state.reads, 0);
    assert.deepEqual(f.state.controls, []);
    f.controller.connect(true);
    assert.deepEqual(f.state.controls, [true]);
    assert.equal(f.state.reads, 0);
    const entering = f.controller.activateAsync();
    await tickAsync();
    assert.equal(f.state.reads, 1);
    assert.equal(new TextDecoder().decode(new Uint8Array(f.state.packets[0], 24)), 'client');
    f.acknowledge();
    await entering;
    await f.controller.enableAsync(false);
    f.controller.connect(false);
    f.controller.connect(true);
    await f.controller.activateAsync();
    assert.equal(f.controller.enabled, false);
    assert.deepEqual(f.state.controls, [true, false]);
    assert.equal(f.state.reads, 1);
    assert.equal(f.state.packets.length, 1);
    assert.deepEqual(f.state.errors, []);
});

test('clipboard format detection ignores images and distinguishes explicit empty text from a missing text format', async () =>
{
    assert.equal(await readDesktopClipboardTextAsync({ read: async () => [{ types: ['image/png'],

        getType: () =>
        {
            throw new Error('Images must not be read.');
        } }] }), null);
    assert.equal(await readDesktopClipboardTextAsync({ read: async () => [{ types: ['text/plain'],

        getType: async () => new Blob(['']) }] }), '');
    assert.equal(await readDesktopClipboardTextAsync({ readText: async () => '' }), null);
    assert.equal(await readDesktopClipboardTextAsync({ readText: async () => 'plain' }), 'plain');
    const f = fixture();
    f.options.read = async () => null;
    f.controller.connect(true);
    await f.controller.enableAsync(true);
    assert.deepEqual(f.state.packets, []);
    await f.controller.pasteAsync();
    assert.deepEqual(f.state.packets, []);
    assert.equal(f.state.pastes, 1);
    assert.deepEqual(f.state.errors, []);
    f.controller.leave();
});

test('unchanged client text preserves host-only clipboard contents across re-entry and reconnection', async () =>
{
    const f = fixture();
    await f.enableAsync();
    for (const reconnect of [false, true])
    {
        f.controller.leave();
        if (reconnect)
        {
            f.controller.connect(false);
            f.controller.connect(true);
        }
        await f.controller.activateAsync();
        await f.controller.pasteAsync('client');
    }
    assert.equal(f.state.packets.length, 1);
    assert.equal(f.state.pastes, 2);
    assert.deepEqual(f.state.errors, []);
    f.controller.leave();
});

test('non-text paste retains host content and releases following input in order', async () =>
{
    const f = fixture();
    await f.enableAsync();
    const reading = Promise.withResolvers();
    f.options.read = () => reading.promise;
    const pasting = f.controller.pasteAsync();
    assert.equal(f.controller.hold(4, 0, 0, 0, 65, 1), true);
    reading.resolve(null);
    await pasting;
    assert.equal(f.state.packets.length, 1);
    assert.deepEqual(f.state.order, ['paste', 'input']);
    assert.deepEqual(f.state.errors, []);
    f.controller.leave();
});

test('text copied after a non-text observation or explicit local copy is fresh even with the same contents', async () =>
{
    for (const explicit of [false, true])
    {
        const f = fixture();
        await f.enableAsync();
        if (explicit)
        {
            f.controller.copied();
        }
        else
        {
            await f.controller.pasteAsync(null);
        }
        const pasting = f.controller.pasteAsync('client');
        await tickAsync();
        assert.equal(f.state.packets.length, 2);
        assert.equal(f.state.pastes, explicit ? 0 : 1);
        f.acknowledge(2);
        await pasting;
        assert.equal(f.state.pastes, explicit ? 1 : 2);
        f.controller.leave();
    }
});

test('non-text read completing after disconnect cannot paste or release queued keys', async () =>
{
    const f = fixture();
    await f.enableAsync();
    const reading = Promise.withResolvers();
    f.options.read = () => reading.promise;
    const pasting = f.controller.pasteAsync();
    f.controller.hold(4, 0, 0, 0, 65, 1);
    f.controller.connect(false);
    reading.resolve(null);
    await pasting;
    assert.equal(f.state.pastes, 0);
    assert.deepEqual(f.state.input, []);
    assert.deepEqual(f.state.errors, []);
});

test('a late host write cannot make unchanged client text appear to be a new local copy', async () =>
{
    const f = fixture();
    await f.enableAsync();
    const writing = Promise.withResolvers();
    f.options.write = async value =>
    {
        f.state.writes.push(value);
        await writing.promise;
        f.state.value = value;
    };
    f.controller.receive(frame(0, 0, 2, 'new host'));
    f.controller.leave();
    const returning = f.controller.activateAsync();
    writing.resolve();
    await returning;
    assert.equal(f.state.value, 'new host');
    assert.deepEqual(f.state.writes, ['new host']);
    await f.controller.pasteAsync();
    assert.equal(f.state.packets.length, 1);
    assert.equal(f.state.pastes, 1);
    f.controller.leave();
});

test('a missing acknowledgement never pastes or replays the queued key', { timeout: 8000 }, async () =>
{
    const f = fixture();
    await f.enableAsync();
    const pasting = f.controller.pasteAsync('unconfirmed');
    await tickAsync();
    f.controller.hold(4, 0, 0, 0, 65, 1);
    await pasting;
    assert.equal(f.state.pastes, 0);
    assert.deepEqual(f.state.input, []);
    assert.deepEqual(f.state.errors, ['Clipboard.Timeout']);
    f.acknowledge(2);
    assert.equal(f.state.pastes, 0);
    f.controller.leave();
});

test('host changes update the client, re-entry prioritizes the client, and late host notifications do not overwrite it', async () =>
{
    const f = fixture();
    await f.enableAsync();
    f.controller.leaveArea();
    f.controller.receive(frame(0, 0, 2, 'host-copy'));
    await tickAsync();
    assert.equal(f.state.value, 'host-copy');
    f.controller.leave();
    f.state.value = 'copied-elsewhere';
    f.controller.receive(frame(0, 0, 3, 'inactive-host-copy'));
    const returning = f.controller.activateAsync();
    await tickAsync();
    assert.equal(new TextDecoder().decode(new Uint8Array(f.state.packets.at(-1), 24)), 'copied-elsewhere');
    f.acknowledge(4);
    await returning;
    f.controller.receive(frame(0, 0, 3, 'late-host-copy'));
    await tickAsync();
    assert.equal(f.state.value, 'copied-elsewhere');
    f.controller.receive(frame(0, 0, 5, 'next-host-copy'));
    await tickAsync();
    assert.equal(f.state.value, 'next-host-copy');
    f.controller.leave();
});

test('paste and following keys are sent only after the matching host acknowledgement', async () =>
{
    const f = fixture();
    await f.enableAsync();
    const pasting = f.controller.pasteAsync('fresh paste');
    await tickAsync();
    assert.equal(f.controller.hold(4, 0, 0, 0, 65, 1), true);
    f.controller.receive(frame(0, 0, 2, 'old host'));
    f.controller.receive(frame(1, 999, 3));
    assert.equal(f.state.pastes, 0);
    assert.deepEqual(f.state.input, []);
    f.acknowledge(4);
    await pasting;
    assert.deepEqual(f.state.order, ['paste', 'input']);
    assert.equal(f.state.pastes, 1);
    assert.equal(f.state.reads, 1);
    assert.deepEqual(f.state.writes, []);
    f.controller.leave();
});

test('host failure and disconnection discard pending paste and keys without replay', async () =>
{
    for (const disconnect of [false, true])
    {
        const f = fixture();
        await f.enableAsync();
        const pasting = f.controller.pasteAsync('pending');
        await tickAsync();
        f.controller.hold(4, 0, 0, 0, 65, 1);
        if (disconnect)
        {
            f.controller.connect(false);
        }
        else
        {
            f.acknowledge(2, 2);
        }
        await pasting;
        f.acknowledge(3);
        assert.equal(f.state.pastes, 0);
        assert.deepEqual(f.state.input, []);
        assert((0) < (f.state.released));
        f.controller.leave();
    }
});

test('a real paste event can transfer text even if asynchronous clipboard reading is denied', async () =>
{
    const f = fixture();
    f.options.read = () => Promise.reject(new Error('NotAllowedError'));
    f.controller.connect(true);
    await f.controller.enableAsync(true);
    assert.deepEqual(f.state.errors, ['Clipboard.Permission']);
    assert.deepEqual(f.state.packets, []);
    const pasting = f.controller.pasteAsync('event text');
    await tickAsync();
    f.acknowledge(7);
    await pasting;
    assert.equal(f.state.pastes, 1);
    f.controller.leave();
});

test('slow browser writes retain only the latest update and stop echoing identical text', async () =>
{
    const f = fixture();
    await f.enableAsync();
    let release;
    f.options.write = value =>
    {
        f.state.writes.push(value);
        if (f.state.writes.length === 1)
        {
            return new Promise(resolve =>
            {
                release = resolve;
            });
        }
        return Promise.resolve();
    };
    for (let version = 2; version <= 100; version++)
    {
        f.controller.receive(frame(0, 0, version, 'host-' + version));
    }
    assert.deepEqual(f.state.writes, ['host-2']);
    release();
    await tickAsync();
    assert.deepEqual(f.state.writes, ['host-2', 'host-100']);
    f.controller.receive(frame(0, 0, 101, 'host-100'));
    await tickAsync();
    assert.equal(f.state.writes.length, 2);
    f.controller.leave();
});

test('permission-blocked host text is bounded and retried only by a gesture in the same active area', async () =>
{
    const f = fixture();
    await f.enableAsync();
    f.options.write = () => Promise.reject(new Error('NotAllowedError'));
    f.controller.receive(frame(0, 0, 2, 'first'));
    await tickAsync();
    f.controller.receive(frame(0, 0, 3, 'latest'));
    await tickAsync();
    assert.deepEqual(f.state.errors, ['Clipboard.WritePermission']);
    f.options.write = async value =>
    {
        f.state.value = value;
    };
    f.controller.retryHost();
    await tickAsync();
    assert.equal(f.state.value, 'latest');
    f.controller.leave();
});

test('a late asynchronous host write cannot win over the clipboard captured on re-entry', async () =>
{
    const f = fixture();
    await f.enableAsync();
    let release;
    f.options.write = value =>
    {
        if (value === 'old-host')
        {
            return new Promise(resolve =>
            {
                release = () =>
                {
                    f.state.value = value;
                    resolve();
                };
            });
        }
        f.state.value = value;
        return Promise.resolve();
    };
    f.controller.receive(frame(0, 0, 2, 'old-host'));
    f.controller.leave();
    f.state.value = 'new-client';
    const returning = f.controller.activateAsync();
    release();
    await tickAsync();
    assert.equal(f.state.value, 'new-client');
    assert.equal(new TextDecoder().decode(new Uint8Array(f.state.packets.at(-1), 24)), 'new-client');
    f.acknowledge(3);
    await returning;
    f.controller.leave();
});

test('clipboard sequence wrap preserves ordering and Shift+Insert keeps the native paste event available', async () =>
{
    const f = fixture();
    await f.enableAsync();
    f.controller.receive(frame(0, 0, 2147483648, 'before wrap'));
    f.controller.receive(frame(0, 0, 4294967295, 'at wrap'));
    await tickAsync();
    f.controller.receive(frame(0, 0, 0, 'after wrap'));
    await tickAsync();
    assert.equal(f.state.value, 'after wrap');
    f.controller.receive(frame(0, 0, 4294967295, 'stale'));
    await tickAsync();
    assert.equal(f.state.value, 'after wrap');
    let prevented = false;
    assert.equal(f.controller.key({ code: 'KeyV', metaKey: true }), false);
    assert.equal(f.controller.key({ code: 'Insert', shiftKey: true,

        preventDefault: () =>
        {
            prevented = true;
        } }), true);
    f.controller.pasted();
    assert.equal(prevented, false);
    f.controller.leave();
});

test('an unfocused browser does not activate clipboard access before blur is delivered', async () =>
{
    const f = fixture();
    let focused = false;
    f.options.focused = () => focused;
    f.controller.connect(true);
    await f.controller.enableAsync(true);
    await f.controller.activateAsync();
    await f.controller.pasteAsync('inactive paste');
    f.controller.receive(frame(0, 0, 1, 'inactive host'));
    assert.equal(f.state.reads, 0);
    assert.deepEqual(f.state.writes, []);
    assert.deepEqual(f.state.packets, []);
    assert.deepEqual(f.state.errors, []);
    focused = true;
    const returning = f.controller.activateAsync();
    await tickAsync();
    f.acknowledge(2);
    await returning;
    assert.equal(f.state.reads, 1);
    f.controller.leave();
});

test('a read rejected after native focus loss stays silent while an active refusal is reported', async () =>
{
    const f = fixture();
    let focused = true;
    f.options.focused = () => focused;
    const pending = Promise.withResolvers();
    f.options.read = () => pending.promise;
    f.controller.connect(true);
    const enabling = f.controller.enableAsync(true);
    focused = false;
    pending.reject(new Error('Document is not focused'));
    await enabling;
    assert.deepEqual(f.state.errors, []);
    assert.deepEqual(f.state.packets, []);
    f.controller.leave();
    focused = true;
    f.options.read = () => Promise.reject(new Error('Permission denied'));
    await f.controller.activateAsync();
    assert.deepEqual(f.state.errors, ['Clipboard.Permission']);
    f.controller.leave();
});

test('a host clipboard write rejected after focus loss does not show a permission warning', async () =>
{
    const f = fixture();
    let focused = true;
    f.options.focused = () => focused;
    await f.enableAsync();
    const pending = Promise.withResolvers();
    f.options.write = () => pending.promise;
    f.controller.receive(frame(0, 0, 2, 'host'));
    focused = false;
    pending.reject(new Error('Document is not focused'));
    await tickAsync();
    assert.deepEqual(f.state.errors, []);
    f.controller.leave();
});
