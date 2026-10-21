// One bounded decoder/presentation queue per displayed monitor. Audio gates presentation,
// with a short decode lead: do not wait until presentation time to start decoding.
import { hevcCodec } from './remote-desktop-hevc.js';
import { createDesktopRenderer } from './remote-desktop-renderer.js';

// VP9 has no in-band level. Declare the smallest level for the requested bitrate,
// frame rate and four tiles instead of requesting an unnecessarily large tier.
const vp9Levels = [[31, 983040, 36864000, 2752, 12000], [40, 2228224, 83558400, 4160, 18000], [41, 2228224, 160432128, 4160, 30000], [50, 8912896, 311951360, 8384, 60000],
    [51, 8912896, 588251136, 8384, 120000], [52, 8912896, 1176502272, 8384, 180000], [60, 35651584, 1176502272, 16832, 180000],
    [61, 35651584, 2353004544, 16832, 240000], [62, 35651584, 4706009088, 16832, 480000]];

/** 映像の復号待ち・参照フレーム・描画の寿命を管理します。
 * @returns 呼出し元が使用する値です。
 */
export function createDesktopVideo(monitor, canvas, options)
{
    const renderer = options.renderer ?? createDesktopRenderer(canvas);
    const packets = new Array(256).fill(null), times = new Float64Array(256), arrivals = new Float64Array(256), keys = new Uint8Array(256);
    const frames = new Array(8).fill(null), frameArrivals = new Float64Array(8);
    const submittedTimes = new Float64Array(64), submittedArrivals = new Float64Array(64);

    // Retain only one bounded compressed GOP, sharing received buffers. A flushed
    // decoder requires a key; replay restores references without restarting capture.
    const gop = new Array(256).fill(null), gopTimes = new Float64Array(256), gopArrivals = new Float64Array(256);
    let gopCount = 0, gopBytes = 0, replay = -1, lastEmitted = -Infinity, keyWaitSince = null;
    let read = 0, count = 0, bytes = 0, frameRead = 0, frameCount = 0, submitted = 0, history = 0;
    let presented = 0, flushStarted = 0, discontinuitySince = null, tailExpected = -Infinity;
    let decodeDelay = 0, drawCost = 0, flushes = 0, replays = 0, lastPresented = -Infinity;

    // The requested fps is a capture ceiling, not the arrival cadence of VFR.
    // A common three-watch-tick grace keeps short bursts from repeatedly flushing
    // and rebuilding the decoder just because the capture ceiling is higher.
    const idleDelay = 75, maximumSubmitted = 16;
    let waitingKey = true, configuring = false, flushing = false, closed = false, epoch = 0, codec = 'h264', accepting = false;
    let lastInput = 0, lastOutput = 0, pendingSince = 0, delay = 0, dropped = 0, preference = options.acceleration, acceptingSince = 0, hasOutput = false, hasDisplayed = false;
    const decoder = new VideoDecoder({
        /** 処理済みのデータを出力先へ渡します。 */
        output(frame)
        {
            if (closed || !options.active())
            {
                frame.close();
                return;
            }
            submitted = Math.max(0, submitted - 1);

            // Replayed reference frames are still decoder progress under load, even
            // though they must not be displayed again.
            lastOutput = performance.now();
            if (frame.timestamp <= lastEmitted)
            {
                frame.close();
                if (count || (0) <= (replay))
                {
                    options.schedule();
                }
                return;
            }
            lastEmitted = frame.timestamp;
            hasOutput = true;
            let arrived = lastOutput;
            for (let i = 0; i < submittedTimes.length; i++)
            {
                if (submittedTimes[i] === frame.timestamp)
                {
                    arrived = submittedArrivals[i];
                    break;
                }
            }
            decodeDelay = lastOutput - arrived;
            if (frameCount === frames.length)
            {
                frames[frameRead].close();
                frames[frameRead] = null;
                frameRead = (frameRead + 1) % frames.length;
                frameCount--;
                dropped++;
            }
            const slot = (frameRead + frameCount) % frames.length;
            frames[slot] = frame;
            frameArrivals[slot] = arrived;
            frameCount++;
            options.schedule();
        },

        error: decoderFailure });
    decoder.ondequeue = () =>
    {
        if (!closed)
        {
            try
            {
                pump();
            }
            catch (error)
            {
                decoderFailure(error);
            }
        }
    };

    /** デコーダーの失敗を現在の実行世代へ通知します。 */
    function decoderFailure(error)
    {
        const expectedH265 = 'h265';

        if (closed || !options.active())
        {
            return;
        }
        if (codec === expectedH265 && options.fallback)
        {
            options.fallback((error.name + ': ' + error.message).slice(0, 240));
            return;
        }
        options.error(error);
    }

    /** 未処理のパケットを待機列から除きます。 */
    function clearPackets()
    {
        packets.fill(null);
        read = count = bytes = 0;
    }

    /** 保留しているフレームを解放します。 */
    function clearFrames()
    {
        for (const frame of frames)
        {
            frame?.close();
        }
        frames.fill(null);
        frameRead = frameCount = 0;
    }

    /** 保持している映像の参照系列を消去します。 */
    function clearGop()
    {
        gop.fill(null);
        gopCount = gopBytes = 0;
        replay = -1;
    }

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        const expectedClosed = 'closed';

        epoch++;
        configuring = flushing = false;
        waitingKey = true;
        submitted = pendingSince = 0;
        discontinuitySince = null;
        tailExpected = lastEmitted;
        clearPackets();
        clearFrames();
        clearGop();
        keyWaitSince = null;
        submittedTimes.fill(-1);
        if (decoder.state !== expectedClosed)
        {
            decoder.reset();
        }
    }

    /** 準備された項目を処理先へ提出します。
     * @returns 呼出し元が使用する値です。
     */
    function submit(payload, key, timestamp, arrived)
    {
        if (!submitted)
        {
            pendingSince = performance.now();
        }
        tailExpected = Math.max(tailExpected, timestamp);
        submittedTimes[history] = timestamp;
        submittedArrivals[history] = arrived;
        history = (history + 1) % submittedTimes.length;
        submitted++;
        try
        {
            decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data: payload }));
            return true;
        }
        catch (error)
        {
            decoderFailure(error);
            return false;
        }
    }

    /** 現在の値から適用する設定を組み立てます。
     * @returns 呼出し元が使用する値です。
     */
    function configuration(payload)
    {
        const expectedVp9 = 'vp9';
        const expectedH2652 = 'h265';

        let name;
        if (codec === expectedVp9)
        {
            const width = monitor.width + monitor.width % 2, height = monitor.height + monitor.height % 2;
            const pixels = width * height, level = vp9Levels.find(value => pixels <= value[1] && pixels * (options.frameRate ?? 30) <= value[2] && Math.max(width, height) <= value[3] && (options.maximumBitrateKbps === null || (options.maximumBitrateKbps ?? 6000) <= value[4]));
            if (!level)
            {
                throw new Error('The selected display exceeds the VP9 decoder size limits.');
            }
            name = 'vp09.00.' + level[0] + '.08';
        }
        else if (codec === expectedH2652)
        {
            name = hevcCodec(payload);
        }
        else
        {
            for (let i = 0; i + 7 < payload.length; i++)
            {
                let start = 0;
                if (payload[i] === 0 && payload[i + 1] === 0 && payload[i + 2] === 1)
                {
                    start = i + 3;
                }
                else if (payload[i] === 0 && payload[i + 1] === 0 && payload[i + 2] === 0 && payload[i + 3] === 1)
                {
                    start = i + 4;
                }
                if (start && (payload[start] & 31) === 7)
                {
                    name = 'avc1.' + Array.from(payload.subarray(start + 1, start + 4), v => v.toString(16).padStart(2, '0')).join('');
                    break;
                }
            }
        }
        return name ? { codec: name, codedWidth: monitor.width + monitor.width % 2, codedHeight: monitor.height + monitor.height % 2,
            optimizeForLatency: true, hardwareAcceleration: options.acceleration } : null;
    }

    /** 現在の入力に対応する処理設定を適用します。
     * @returns 呼出し元が使用する値です。
     */
    function configure(payload)
    {
        const config = configuration(payload);
        if (!config)
        {
            return false;
        }
        if (!VideoDecoder.isConfigSupported)
        {
            try
            {
                decoder.configure(config);
                return true;
            }
            catch (error)
            {
                decoderFailure(error);
                return false;
            }
        }
        configuring = true;
        const run = epoch;
        (async () =>
        {
            const expectedPreferHardware = 'prefer-hardware';
            const expectedH2653 = 'h265';

            const preferences = config.hardwareAcceleration === expectedPreferHardware ? ['prefer-hardware', 'no-preference'] : [config.hardwareAcceleration];
            let failure;
            for (const requested of preferences)
            {
                config.hardwareAcceleration = requested;
                const support = await VideoDecoder.isConfigSupported(config);
                if (closed || run !== epoch)
                {
                    return;
                }
                if (!support.supported)
                {
                    continue;
                }
                try
                {
                    // Probe success is not an initialization guarantee. Use the
                    // recognized configuration and try the standard decoder path
                    // if hardware initialization is rejected by the platform.
                    decoder.configure(support.config ?? config);
                }
                catch (error)
                {
                    const expectedNotsupportederror = 'NotSupportedError';
                    const expectedClosed2 = 'closed';

                    failure = error;
                    if (error.name === expectedNotsupportederror && decoder.state !== expectedClosed2)
                    {
                        continue;
                    }
                    throw error;
                }
                preference = requested;
                configuring = false;
                pump();
                options.schedule();
                return;
            }
            const reason = `${config.codec} · ${config.codedWidth}×${config.codedHeight} · ${config.hardwareAcceleration}: ${failure?.message ?? 'unsupported'}`;
            if (codec === expectedH2653 && options.fallback)
            {
                configuring = false;
                options.fallback(reason);
                return;
            }
            throw failure instanceof Error ? failure : new Error('The selected video codec/decoder is not supported by this browser.', { cause: failure });
        })().catch(error =>
        {
            if (!closed && run === epoch)
            {
                decoderFailure(error);
            }
        });
        return false;
    }

    /** 再開時に必要な参照フレームを上限内で保持します。 */
    function retainReference(payload, key, slot)
    {
        if (key)
        {
            clearGop();
        }
        if (gopCount === gop.length || (8 * 1024 * 1024) < (gopBytes + payload.byteLength))
        {
            clearGop();
        }
        else if (key || gopCount)
        {
            gop[gopCount] = payload;
            gopTimes[gopCount] = times[slot];
            gopArrivals[gopCount++] = arrivals[slot];
            gopBytes += payload.byteLength;
        }
    }

    /** 一つの映像パケットを処理し、待機が必要かを返します。
     * @returns 次のパケットへ進める場合はtrueです。
     */
    function pumpFrame()
    {
        const expectedUnconfigured = 'unconfigured';

        if ((0) <= (replay))
        {
            replays++;
            const index = replay++;
            if (replay === gopCount)
            {
                replay = -1;
            }
            if (!submit(gop[index], index === 0, gopTimes[index], gopArrivals[index]))
            {
                return false;
            }
            return true;
        }
        const slot = read, key = keys[slot] === 1, payload = packets[slot];
        if (waitingKey && !key && gopCount)
        {
            waitingKey = false;
            replay = 0;
            return true;
        }
        if (waitingKey && key && decoder.state === expectedUnconfigured && !configure(payload))
        {
            return false;
        }
        const audible = options.audible?.();
        if (audible != null && (audible + 33000) < (times[slot]))
        {
            return false;
        }
        packets[slot] = null;
        count--;
        bytes -= payload.byteLength;
        read = (slot + 1) % packets.length;
        if (waitingKey)
        {
            if (!key)
            {
                dropped++;
                keyWaitSince ??= performance.now();
                return true;
            }
            waitingKey = false;
        }
        keyWaitSince = null;
        retainReference(payload, key, slot);
        return submit(payload, key, times[slot], arrivals[slot]);
    }

    /** 処理能力の範囲内で待機列から次の項目を進めます。 */
    function pump()
    {
        const expectedClosed3 = 'closed';

        if (closed || configuring || flushing || !accepting)
        {
            return;
        }
        while ((count || (0) <= (replay)) && decoder.state !== expectedClosed3 && decoder.decodeQueueSize < 2 && submitted < maximumSubmitted)
        {
            if (!pumpFrame())
            {
                return;
            }
        }
    }

    /** 入力が途切れた際の残存処理を進めます。 */
    function drainIdle(now)
    {
        const expectedConfigured = 'configured';

        // A decoder may retain outputs after consuming its input queue. When the
        // in-flight limit is full, a queued independent key can resume after flush
        // without replaying that full window. Waiting for count==0 would deadlock.
        if (!submitted || (0) <= (replay) || configuring || flushing || decoder.state !== expectedConfigured || decoder.decodeQueueSize)
        {
            return;
        }
        let independentOffset = -1;
        if ((maximumSubmitted) <= (submitted))
        {
            for (let index = 0; index < count; index++)
            {
                if (keys[(read + index) % packets.length] === 1)
                {
                    independentOffset = index;
                    break;
                }
            }
        }
        if (count && independentOffset < 0)
        {
            return;
        }
        const progress = Math.max(pendingSince, lastOutput, (0) <= (independentOffset) ? 0 : lastInput);
        if (now - progress < idleDelay)
        {
            return;
        }

        // Some decoders retain the tail of a VFR stream even with optimizeForLatency.
        // flush releases it, but the next submission MUST be a key frame per WebCodecs.
        if (!decoder.flush)
        {
            return;
        }

        // Only unsubmitted deltas before an already received independent key are
        // obsolete. Keep decoder output and the key's following dependency chain.
        for (let index = 0; index < independentOffset; index++)
        {
            bytes -= packets[read].byteLength;
            packets[read] = null;
            read = (read + 1) % packets.length;
            count--;
            dropped++;
        }
        flushing = true;
        flushes++;
        flushStarted = now;
        const run = epoch;
        decoder.flush().then(() =>
        {
            if (closed || run !== epoch)
            {
                return;
            }
            flushing = false;
            waitingKey = true;
            submitted = 0;
            pendingSince = performance.now();
            pump();
            options.schedule();
        }).catch(error =>
        {
            if (!closed && run === epoch)
            {
                decoderFailure(error);
            }
        });
    }

    return {
        decoder,

        /** 使用するコーデック情報を更新します。 */
        setCodec(value)
        {
            codec = value;
            accepting = true;
            acceptingSince = performance.now();
        },

        /** 参照関係が途切れた映像を再開可能な状態へ戻します。 */
        discontinuity()
        {
            if (!closed && accepting)
            {
                discontinuitySince ??= performance.now();
            }
        },

        /** 受信データを検証し、現在の状態へ取り込みます。 */
        receive(payload, key, timestamp, arrived = performance.now())
        {
            if (closed || !accepting)
            {
                return;
            }
            if (key)
            {
                discontinuitySince = null;
            }
            const now = performance.now();
            lastInput = now;
            const budget = Math.min(1500, Math.max(150, options.bufferBudget?.() ?? 150)) * 1000;

            // A VFR timestamp gap or a burst delivered together is not itself a
            // decoder backlog. Give newly arrived references time to be consumed.
            if (count === packets.length || (8 * 1024 * 1024) < (bytes + payload.byteLength) || (count && (budget) < (timestamp - times[read]) && (budget) < ((now - arrivals[read]) * 1000)))
            {
                dropped += count;
                reset();
            }

            // A fresh packet may wait briefly for audio/configuration. Its deadline
            // starts here, not at the last image before a long static interval.
            if (!count && !submitted && replay < 0)
            {
                pendingSince = now;
            }
            const slot = (read + count) % packets.length;
            packets[slot] = payload;
            times[slot] = timestamp;
            arrivals[slot] = arrived;
            keys[slot] = key ? 1 : 0;
            count++;
            bytes += payload.byteLength;
            pump();
            options.schedule();
        },

        /** 準備された値を現在の描画領域へ反映します。
         * @returns 呼出し元が使用する値です。
         */
        draw(audible)
        {
            let chosen, arrived;
            pump();
            if (renderer.ready && !renderer.ready())
            {
                return (0) < (frameCount) || (0) < (count);
            }
            while (frameCount)
            {
                const next = frames[frameRead];
                if (audible != null && (audible + 3000) < (next.timestamp))
                {
                    break;
                }
                chosen?.close();
                chosen = next;
                arrived = frameArrivals[frameRead];
                frames[frameRead] = null;
                frameRead = (frameRead + 1) % frames.length;
                frameCount--;
            }
            if (chosen)
            {
                let retained = false;
                try
                {
                    const started = performance.now();
                    retained = renderer.draw(chosen, monitor.width, monitor.height) === true;
                    const elapsed = performance.now() - started;
                    drawCost = presented ? drawCost * 0.9 + elapsed * 0.1 : elapsed;
                    delay = performance.now() - arrived;
                    hasDisplayed = true;
                    presented++;
                    lastPresented = chosen.timestamp;
                }
                finally
                {
                    if (!retained)
                    {
                        chosen.close();
                    }
                }
            }
            return (0) < (frameCount) || (0) < (count);
        },

        /** 経過時間に応じて状態と期限を更新します。 */
        tick(now)
        {
            if (closed || !accepting)
            {
                return;
            }
            renderer.tick?.();
            drainIdle(now);
            const audible = options.audible?.();
            const audioWaiting = !configuring && (0) < (count) && audible != null && (audible + 33000) < (times[read]);
            let stalled;
            switch (true)
            {
                case Boolean(flushing && (1500) < (now - Math.max(flushStarted, lastOutput))):
                    {
                        stalled = 'decoder-flush';
                    }
                    break;
                case Boolean(discontinuitySince != null && (1500) < (now - discontinuitySince)):
                    {
                        stalled = 'capture-reference';
                    }
                    break;
                case Boolean((submitted || lastEmitted < tailExpected || count && !audioWaiting) && (1500) < (now - Math.max(pendingSince, lastOutput))):
                    {
                        stalled = 'decoder-output';
                    }
                    break;
                case Boolean(keyWaitSince != null && (1500) < (now - keyWaitSince)):
                    {
                        stalled = 'keyframe-wait';
                    }
                    break;
                case Boolean(!hasOutput && !audioWaiting && (3000) < (now - acceptingSince)):
                    {
                        stalled = 'first-frame';
                    }
                    break;
            }
            if (stalled)
            {
                reset();
                options.refresh(stalled);
            }
        },

        get delay()
        {
            return delay;
        },

        get hasFrames()
        {
            return (0) < (frameCount);
        },

        get hasPending()
        {
            return (0) < (frameCount) || (0) < (count) || (0) <= (replay);
        },

        get hasDisplayed()
        {
            return hasDisplayed;
        },

        get dropped()
        {
            return dropped;
        },

        get presented()
        {
            return presented;
        },

        get preference()
        {
            return preference;
        },

        get renderer()
        {
            return renderer.name;
        },

        get decodeDelay()
        {
            return decodeDelay;
        },

        get drawCost()
        {
            return drawCost;
        },

        get flushes()
        {
            return flushes;
        },

        get replays()
        {
            return replays;
        },

        get lastPresented()
        {
            return lastPresented;
        },

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            const expectedClosed4 = 'closed';

            closed = true;
            epoch++;
            clearPackets();
            clearFrames();
            clearGop();
            decoder.ondequeue = null;
            if (decoder.state !== expectedClosed4)
            {
                decoder.close();
            }
            renderer.close();
        }
    };
}
