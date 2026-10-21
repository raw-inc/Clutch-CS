import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createDesktopOpus, supportsDesktopOpus } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-opus.js';

/** 試験で受信させるパケットを組み立てます。
 * @returns 呼出し元が使用する値です。
 */
function packet(timestamp, frames = 960, skip = 0, reset = false)
{
    const buffer = new ArrayBuffer(36), view = new DataView(buffer);
    view.setInt32(0, 10);
    view.setInt32(8, 12);
    view.setInt32(12, reset ? 1 : 0);
    view.setBigInt64(16, BigInt(timestamp));
    view.setInt32(24, frames);
    view.setInt32(28, skip);
    return buffer;
}

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns audio・decoders・output・errors・closedFramesを含む結果です。
 */
function fixture(t)
{
    const names = ['AudioDecoder', 'EncodedAudioChunk'], saved = names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]);
    const decoders = [], output = [], errors = [];
    let closedFrames = 0;
    globalThis.EncodedAudioChunk = class
    {
        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(value)
        {
            Object.assign(this, value);
        }
    };
    globalThis.AudioDecoder = class
    {
        /** 試験で指定したデコーダー構成の対応状況を返します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        static async isConfigSupported()
        {
            return { supported: true };
        }

        /** 試験で操作する状態とブラウザー境界を初期化します。 */
        constructor(callbacks)
        {
            this.callbacks = callbacks;
            this.chunks = [];
            this.finished = new Set();
            this.state = 'unconfigured';
            decoders.push(this);
        }

        /** 現在の入力に対応する処理設定を適用します。 */
        configure(config)
        {
            assert.equal(config.codec, 'opus');
            this.state = 'configured';
        }

        /** 試験用の復号要求を保持して出力を制御します。 */
        decode(chunk)
        {
            assert.equal(chunk.type, 'key');
            this.chunks.push(chunk);
        }

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            this.state = 'closed';
        }

        /** 保持している入力または描画要求を送出します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async flush()
        {
            for (let i = 0;i < this.chunks.length;i++)
            {
                if (!this.finished.has(i))
                {
                    this.output(i);
                }
            }
        }

        /** 処理済みのデータを出力先へ渡します。 */
        output(index = 0)
        {
            this.finished.add(index);
            const timestamp = this.chunks[0].timestamp + index * 20000;
            this.callbacks.output({ timestamp, sampleRate: 48000, numberOfChannels: 2, numberOfFrames: 960,

                /** 試験データを指定された出力領域へコピーします。 */
                copyTo(target, options)
                {
                    assert.equal(target.length, options.frameCount * 2);
                    target.fill(0.25);
                    this.copied = options;
                },

                /** 対象を閉じ、関連する状態を整理します。 */
                close()
                {
                    closedFrames++;
                } });
        }
    };
    const audio = createDesktopOpus(buffer => output.push(buffer), error => errors.push(error));
    t.after(() =>
    {
        audio.close();
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
    return { audio, decoders, output, errors,

        get closedFrames()
        {
            return closedFrames;
        } };
}

test('raw packets decode into trimmed timestamped PCM and every AudioData is closed', async t =>
{
    const f = fixture(t);
    assert.equal(await supportsDesktopOpus(), true);
    f.audio.receive(packet(1000000, 648, 312, true));
    f.decoders[0].output();
    const value = f.output[0], header = new DataView(value);
    assert.equal(value.byteLength, 24 + 648 * 8);
    assert.equal(header.getInt32(0), 5);
    assert.equal(header.getBigInt64(16), 1000000n);
    assert.equal(header.getInt32(12), 1);
    assert.equal(f.closedFrames, 1);
    assert.equal(f.errors.length, 0);
    f.audio.receive(packet(1013500));
    f.decoders[0].output(1);
    assert.equal(new DataView(f.output[1]).getInt32(12), 0);
    assert.equal(new DataView(f.output[1]).getBigInt64(16), 1013500n);
    assert.equal(f.errors.length, 0);
});
test('resume discards stale decoder output and starts at the next packet', t =>
{
    const f = fixture(t);
    f.audio.receive(packet(1000000));
    f.audio.reset();
    f.decoders[0].output();
    assert.equal(f.output.length, 0);
    assert.equal(f.closedFrames, 1);
    f.audio.receive(packet(9000000));
    f.decoders[1].output();
    assert.equal(new DataView(f.output[0]).getBigInt64(16), 9000000n);
    f.audio.close();
    f.audio.receive(packet(9020000));
    assert.equal(f.decoders[1].chunks.length, 1);
});
test('decoder backlog is bounded and malformed sample ranges are rejected', t =>
{
    const f = fixture(t);
    for (let i = 0;i < 17;i++)
    {
        f.audio.receive(packet(1000000 + i * 20000));
    }
    assert.equal(f.decoders.length, 2);
    assert.equal(f.decoders[0].state, 'closed');
    assert.equal(f.decoders[1].chunks.length, 1);
    assert.throws(() => f.audio.receive(packet(2000000, 960, 1)), /sample range/);
    assert.throws(() => f.audio.receive(packet(2000000, 0, 0)), /sample range/);
});
test('unsupported browser advertises no Opus support and a closed decoder can be released', async t =>
{
    const f = fixture(t);
    AudioDecoder.isConfigSupported = async() => ({ supported: false });
    assert.equal(await supportsDesktopOpus(), false);
    f.audio.receive(packet(1000000));
    f.decoders[0].state = 'closed';
    assert.doesNotThrow(() => f.audio.close());
});

test('encoder epoch transition drains valid tail before resetting decoder state', async t =>
{
    const f = fixture(t);
    f.audio.receive(packet(1000000, 312, 0));
    f.audio.receive(packet(1006500, 648, 312, true));
    await Promise.resolve();
    await Promise.resolve();
    assert.equal(f.output.length, 1);
    assert.equal(f.decoders.length, 2);
    f.decoders[1].output();
    assert.equal(f.output.length, 2);
    assert.deepEqual(f.output.map(value => (value.byteLength - 24) / 8), [312, 648]);
    assert.equal(f.closedFrames, 2);
});
