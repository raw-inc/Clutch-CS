import { createDesktopOpus, supportsDesktopOpus } from './remote-desktop-opus.js';

/** 音声の受信・復号・再生・解放を構成します。
 * @returns 呼出し元が使用する値です。
 */
export function createDesktopAudio(maxBufferSamples = 12000, options = {})
{
    if (!Number.isInteger(maxBufferSamples) || maxBufferSamples < 960 || (48000) < (maxBufferSamples))
    {
        throw new RangeError('Invalid audio buffer sample count');
    }
    const context = new AudioContext({ latencyHint: 'interactive', sampleRate: 48000 });
    let node, gain, clock, clockReceived = 0, closed = false, closing, outputDelay = 0, opus, format = null;
    return {
        /** 必要な資源を準備して処理を開始します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async start()
        {
            const expectedOpus = 'opus';

            if (options.codec === expectedOpus && !await supportsDesktopOpus())
            {
                throw new Error('Opus decoding is unavailable in this browser. Select PCM.');
            }
            await context.resume();
            await context.audioWorklet.addModule(new URL('./remote-desktop-audio-worklet.js', import.meta.url));
            if (closed)
            {
                return;
            }
            node = new AudioWorkletNode(context, 'contents-desktop-audio', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2], processorOptions: { maxBufferSamples } });
            gain = context.createGain();
            node.connect(gain);
            gain.connect(context.destination);
            node.port.onmessage = event =>
            {
                clock = event.data;
                clockReceived = performance.now();
            };
        },

        /** 受け取った形式を処理側の設定へ反映します。 */
        format(value)
        {
            const expectedOpus2 = 'opus';

            if (value.sampleRate !== 48000 || value.channels !== 2 || value.format !== (options.codec ?? 'f32le'))
            {
                throw new Error('Unsupported desktop audio format.');
            }
            if (format === value.format)
            {
                return;
            }
            opus?.close();
            opus = null;
            format = value.format;
            if (format === expectedOpus2)
            {
                opus = createDesktopOpus((buffer, ended) =>
                {
                    if (!closed)
                    {
                        node?.port.postMessage(buffer, [buffer]);
                        if (ended)
                        {
                            node?.port.postMessage({ type: 'drain' });
                        }
                    }
                }, error => options.error?.(error));
            }
        },

        /** 受信データを検証し、現在の状態へ取り込みます。 */
        receive(buffer)
        {
            const expectedRunning = 'running';

            if (closed || !node || context.state !== expectedRunning || !format)
            {
                return;
            }
            if (opus)
            {
                opus.receive(buffer);
            }
            else
            {
                node.port.postMessage(buffer, [buffer]);
            }
        },

        /** 音声のミュート状態を反映します。 */
        mute(muted)
        {
            if (gain)
            {
                gain.gain.setValueAtTime(muted ? 0 : 1, context.currentTime);
            }
        },

        /** 許容する音声の先行量を設定します。
         * @returns 呼出し元が使用する値です。
         */
        bufferBudget()
        {
            return Math.min(1500, Math.max(150, maxBufferSamples / 48 + outputDelay * 1000 + 80));
        },

        /** 一時状態を初期値へ戻します。 */
        reset()
        {
            clock = null;
            opus?.reset();
            if (!closed)
            {
                node?.port.postMessage({ type: 'reset' });
            }
        },

        /** 一時停止している処理を再開します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
         */
        async resume()
        {
            const expectedRunning2 = 'running';

            if (closed || context.state === expectedRunning2)
            {
                return;
            }
            this.reset();
            await context.resume();
        },

        /** 現在の音声再生位置を取得します。
         * @returns 呼出し元が使用する値です。
         */
        playhead()
        {
            const expectedRunning3 = 'running';

            if (!clock || closed || context.state !== expectedRunning3 || (250) < (performance.now() - clockReceived) || (0.25) < (context.currentTime - clock.context))
            {
                return null;
            }

            // The worklet keeps reporting its held position during an underrun.
            // Those fresh notifications are not an advancing audible clock. WASAPI
            // may stop supplying packets when all render streams become idle.
            if (!((0) < (clock.rate)))
            {
                return null;
            }
            const output = context.getOutputTimestamp?.();
            let audible = context.currentTime - (context.baseLatency || 0) - (context.outputLatency || 0);
            const elapsed = performance.now() - output?.performanceTime;
            if ((0) < (output?.contextTime) && (0) <= (elapsed) && elapsed < 250)
            {
                audible = output.contextTime + elapsed / 1000;
            }
            outputDelay = Math.max(0, context.currentTime - audible);
            if ((0.4) < (outputDelay))
            {
                return null;
            }
            return clock.source + (audible - clock.context) * 1e6 * clock.rate;
        },

        /** 対象を閉じ、関連する状態を整理します。
         * @returns 呼出し元が使用する値です。
         */
        close()
        {
            if (closed)
            {
                return closing;
            }
            closed = true;
            clock = null;
            opus?.close();
            opus = null;
            if (node)
            {
                node.port.onmessage = null;
                node.port.close();
                node.disconnect();
            }
            gain?.disconnect();
            closing = context.close();
            return closing;
        }
    };
}
