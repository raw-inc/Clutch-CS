import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/remote-desktop-audio-worklet.js', import.meta.url), 'utf8');

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns processor・channels・context・clock・render・appendを含む結果です。
 */
function fixture(maxBufferSamples = 9600)
{
    let Processor, latestClock;
    const context = { sampleRate: 48000, currentTime: 0, ArrayBuffer, DataView, Float32Array, Float64Array,
        AudioWorkletProcessor: class
        {
            /** 試験で操作する状態とブラウザー境界を初期化します。 */
            constructor()
            {
                this.port = {
                    /** 試験対象へのメッセージを記録または配送します。 */
                    postMessage(value)
                    {
                        latestClock = value;
                    } };
            }
        },

        /** 試験で起動するAudioWorkletの型を登録します。 */
        registerProcessor(name, value)
        {
            assert.equal(name, 'contents-desktop-audio');
            Processor = value;
        } };
    vm.runInNewContext(source, context);
    const processor = new Processor({ processorOptions: { maxBufferSamples } }), channels = [new Float32Array(128), new Float32Array(128)], outputs = [channels];
    let rendered = 0;
    return { processor, channels, context,

        clock: () => latestClock,

        /** 現在の値を表示内容へ反映します。
         * @returns 呼出し元が使用する値です。
         */
        render()
        {
            context.currentTime = rendered / 48000;
            processor.process([], outputs);
            rendered += 128;
            return channels;
        },

        /** 受け取った項目を保持する順序へ追加します。 */
        append(timestamp, value = 0.25, reset = false, silent = false, frames = 480)
        {
            const data = new ArrayBuffer(24 + (silent ? 8 : frames * 8)), header = new DataView(data);
            header.setInt32(0, silent ? 6 : 5);
            header.setInt32(12, reset ? 1 : 0);
            header.setBigInt64(16, BigInt(Math.round(timestamp)));
            if (silent)
            {
                header.setInt32(24, frames);
            }
            else
            {
                const samples = new Float32Array(data, 24);
                for (let i = 0; i < frames; i++)
                {
                    samples[i * 2] = samples[i * 2 + 1] = typeof value === 'function' ? value(i) : value;
                }
            }
            processor.port.onmessage({ data });
        }
    };
}

test('audio waits for the configured prebuffer and publishes the held source clock', () =>
{
    const f = fixture(1920);
    f.append(1_000_000);
    for (let i = 0; i < 4; i++)
    {
        assert.ok(f.render()[0].every(sample => sample === 0));
    }
    assert.equal(f.clock().rate, 0);
    assert.equal(f.clock().source, 1_000_000);
    f.append(1_010_000);
    const output = f.render();
    assert.ok(output[0].every(sample => sample === 0.25));
    assert.deepEqual(output[0], output[1]);
    assert.ok((0) < (f.clock().rate));
});

for (const mode of ['timestamp jitter', 'capture discontinuity'])
{
    test(`${mode} does not turn buffered continuous audio into periodic silence`, () =>
    {
        const f = fixture(19200);
        let captured = 0, packet = 0, zeros = 0, previous = 0, biggestStep = 0;
        for (let block = 0; block < 10 * 48000 / 128; block++)
        {
            const elapsed = block * 128;
            while (captured <= elapsed)
            {
                const frames = [240, 480, 960][packet % 3], offset = captured;
                const jitter = mode === 'timestamp jitter' ? (packet % 2 ? 3000 : -3000) : 0;
                f.append(1_000_000 + captured * 1e6 / 48000 + jitter,
                    i => 0.4 + 0.2 * Math.sin((offset + i) * 2 * Math.PI * 440 / 48000),
                    mode === 'capture discontinuity' && packet % 37 === 0, false, frames);
                captured += frames;
                packet++;
            }
            const output = f.render()[0];
            if ((48000) < (elapsed))
            {
                for (const value of output)
                {
                    if (value === 0)
                    {
                        zeros++;
                    }
                    if (previous)
                    {
                        biggestStep = Math.max(biggestStep, Math.abs(value - previous));
                    }
                    previous = value;
                }
            }
        }
        assert.equal(zeros, 0, 'valid queued PCM must survive timestamp corrections');
        assert.ok(biggestStep < 0.02, `waveform discontinuity: ${biggestStep}`);
    });
}

test('a short capture gap preserves queued PCM and changes the source clock at the playback boundary', () =>
{
    const f = fixture(1920);
    f.append(1_000_000, 0.2);
    f.append(1_010_000, 0.2);
    f.append(1_030_000, 0.6, true);
    const first = f.render()[0];
    assert.ok(first.every(value => Math.abs(value - 0.2) < 1e-6));
    assert.equal(f.clock().source, 1_000_000);
    for (let i = 0; i < 8; i++)
    {
        f.render();
    }
    assert.ok(f.channels[0].every(value => Math.abs(value - 0.6) < 1e-6));
    for (let i = 0; i < 8; i++)
    {
        f.render();
    }
    assert.equal(f.clock().source, 1_040_000);
    assert.equal(f.clock().rate, 0);
});

test('visibility recovery discards pre-suspension PCM and establishes a fresh source clock', () =>
{
    const f = fixture();
    f.append(1000000, 0.8);
    f.processor.port.onmessage({ data: { type: 'reset' } });
    assert.equal(f.processor.origin, null);
    f.append(9000000, 0.2);
    for (let i = 0;i < 12;i++)
    {
        f.render();
    }
    assert.ok(f.channels[0].every(value => value === 0 || Math.abs(value - 0.2) < 1e-6));
    assert.equal(f.processor.origin, 9000000);
});

test('discontinuity and compact silence never replay old samples', () =>
{
    const f = fixture();
    f.append(1_000_000, 0.8);
    for (let i = 0; i < 8; i++)
    {
        f.render();
    }
    f.append(6_000_000, 0, true, true);
    for (let i = 0; i < 12; i++)
    {
        assert.ok(f.render()[0].every(sample => sample === 0));
    }
    assert.equal(f.processor.pcm.length, 9600 * 2);
    assert.equal(f.processor.tags.length, 9600);
});

for (const drift of [-0.0002, 0.0002])
{
    test(`capture/playback drift ${drift * 1e6} ppm stays bounded for two simulated minutes`, () =>
    {
        const f = fixture(), duration = 120, actualRate = 48000 * (1 + drift);
        let nextPacket = 0, sampleFrame = 0, missing = 0, checked = 0;
        for (let block = 0; block < duration * 48000 / 128; block++)
        {
            const elapsed = block * 128 / 48000;
            while (nextPacket <= elapsed)
            {
                f.append(10_000_000 + nextPacket * 1e6);
                sampleFrame += 480;
                nextPacket = sampleFrame / actualRate;
            }
            const output = f.render()[0];
            if ((2) < (elapsed))
            {
                for (const value of output)
                {
                    checked++;
                    if ((0.001) < (Math.abs(value - 0.25)))
                    {
                        missing++;
                    }
                }
            }
        }
        assert.equal(missing, 0, `dropout ratio: ${missing / checked}`);
        const clock = f.clock(), observed = clock.source + (f.context.currentTime - clock.context) * 1e6 * clock.rate;
        const lag = 10_000_000 + f.context.currentTime * 1e6 - observed;
        assert.ok((70_000) <= (lag) && lag < 150_000, `accumulated delay: ${lag} us`);
        assert.equal(f.processor.pcm.length, 9600 * 2);
    });
}

test('a larger buffer absorbs 80 ms delivery bursts without steady playback holes', () =>
{
    const f = fixture(19200);
    let captured = 0, burst = -1, missing = 0;
    for (let block = 0;block < 5 * 48000 / 128;block++)
    {
        const elapsed = block * 128 / 48000, currentBurst = Math.floor(elapsed / 0.08);
        if (currentBurst !== burst)
        {
            burst = currentBurst;
            while (captured / 48000 <= elapsed - 0.04)
            {
                f.append(1000000 + captured * 1e6 / 48000);
                captured += 480;
            }
        }
        const output = f.render()[0];
        if ((1) < (elapsed))
        {
            missing += output.filter(value => (0.001) < (Math.abs(value - 0.25))).length;
        }
    }
    assert.equal(missing, 0);
    assert.equal(f.processor.pcm.length, 38400);
});

test('underrun freezes the source clock then resumes after rebuffering', () =>
{
    const f = fixture(1920);
    f.append(1000000);
    f.append(1010000);
    for (let i = 0;i < 20;i++)
    {
        f.render();
    }
    const stopped = f.processor.read;
    for (let i = 0;i < 20;i++)
    {
        f.render();
    }
    assert.equal(f.processor.read, stopped);
    assert.equal(f.clock().rate, 0);
    f.append(1020000);
    f.append(1030000);
    f.append(1040000);
    f.render();
    assert.ok(f.channels[0].every(value => value === 0.25));
    assert.ok((stopped) < (f.processor.read));
});

test('input bursts cannot retain more than the configured sample capacity', () =>
{
    const f = fixture(1920);
    for (let i = 0;i < 100;i++)
    {
        f.append(1000000 + i * 10000);
    }
    assert.ok(f.processor.lastEnd - f.processor.read <= 1920);
    assert.equal(f.processor.pcm.length, 3840);
    assert.equal(f.processor.tags.length, 1920);
    f.render();
    assert.ok(f.channels[0].every(value => value === 0.25));
});

test('a completed short Opus burst drains below the normal prebuffer threshold', () =>
{
    const f = fixture(12000);
    f.append(1000000, 0.3, false, false, 50);
    f.processor.port.onmessage({ data: { type: 'drain' } });
    const audio = f.render()[0];
    assert.ok(audio.subarray(0, 45).every(value => Math.abs(value - 0.3) < 1e-6));
    assert.ok(audio.subarray(55).every(value => value === 0));
    assert.equal(f.processor.buffering, true);
    f.append(2000000, 0.4, false, false, 50);
    f.processor.port.onmessage({ data: { type: 'drain' } });
    assert.ok(f.render()[0].subarray(0, 45).every(value => Math.abs(value - 0.4) < 1e-6));
});
