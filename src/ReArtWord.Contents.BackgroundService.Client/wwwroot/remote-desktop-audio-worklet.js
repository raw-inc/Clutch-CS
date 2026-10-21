// All arrays are allocated once. Incoming transferable PCM is copied into a bounded ring;
// process() never creates an AudioBuffer, array, timer or per-sample object.
/** 受信音声を再生し、消費した音声の時計を共有します。 */
class DesktopAudioProcessor extends AudioWorkletProcessor
{
    /** 再生待ち音声と送信元の時計を初期化します。 */
    constructor(options = {})
    {
        super();
        this.capacity = options.processorOptions?.maxBufferSamples ?? 12000;
        if (!Number.isInteger(this.capacity) || this.capacity < 960 || (48000) < (this.capacity))
        {
            throw new RangeError('Invalid audio buffer sample count');
        }
        this.pcm = new Float32Array(this.capacity * 2);
        this.tags = new Float64Array(this.capacity);
        this.tags.fill(-1);
        this.times = new Float64Array(this.capacity);
        this.endTime = 0;
        this.origin = null;
        this.read = 0;
        this.lastEnd = -1;
        this.rate = 1;
        this.occupancy = 0;
        this.blocks = 0;
        this.target = Math.floor(this.capacity / 2);
        this.buffering = true;
        this.port.onmessage = event =>
        {
            const expectedReset = 'reset';
            const expectedDrain = 'drain';

            if (event.data?.type === expectedReset)
            {
                this.origin = null;
                this.tags.fill(-1);
                this.lastEnd = -1;
                this.read = 0;
                this.buffering = true;
            }
            else if (event.data?.type === expectedDrain)
            {
                if ((this.read) < (this.lastEnd))
                {
                    this.buffering = false;
                }
            }
            else
            {
                this.append(event.data);
            }
        };
    }

    /** 一時状態を初期値へ戻します。 */
    reset(timestamp)
    {
        this.tags.fill(-1);
        this.origin = timestamp;
        this.read = 0;
        this.lastEnd = -1;
        this.rate = 1;
        this.occupancy = 0;
        this.buffering = true;
    }

    /** 受け取った項目を保持する順序へ追加します。 */
    append(buffer)
    {
        if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 32 || (buffer.byteLength - 24) % 8)
        {
            return;
        }
        const header = new DataView(buffer), timestamp = Number(header.getBigInt64(16));
        const silent = header.getInt32(0) === 6;
        const samples = new Float32Array(buffer, 24), frames = silent ? header.getInt32(24) : samples.length / 2;
        if (frames < 1 || (8192) < (frames) || !Number.isFinite(timestamp))
        {
            return;
        }

        // Transport is ordered. A capture glitch describes a boundary, not invalid PCM
        // already queued before it. Only a new epoch (or explicit visibility reset)
        // discards that queue; small clock corrections must never restart the prebuffer.
        if (this.origin === null || (0) <= (this.lastEnd) && (Math.max(250000, this.capacity * 1e6 / 48000)) < (Math.abs(timestamp - this.endTime)))
        {
            this.reset(timestamp);
        }
        const start = Math.max(0, this.lastEnd);
        let packetTime = timestamp;
        if ((0) <= (this.lastEnd) && !header.getInt32(12) && Math.abs(timestamp - this.endTime) < 20000)
        {
            packetTime = this.endTime + (timestamp - this.endTime) * 0.002;
        }
        if ((this.capacity) < (start + frames - this.read))
        {
            // Drop only the oldest buffered samples. Never grow the ring or play
            // overwritten samples after a burst exceeds the configured maximum.
            this.read = start + frames - this.target;
            this.buffering = true;
        }
        for (let frame = 0; frame < frames; frame++)
        {
            const position = start + frame;
            if (position < Math.floor(this.read))
            {
                continue;
            }
            const slot = ((position % this.capacity) + this.capacity) % this.capacity;
            this.pcm[slot * 2] = silent ? 0 : samples[frame * 2];
            this.pcm[slot * 2 + 1] = silent ? 0 : samples[frame * 2 + 1];
            this.tags[slot] = position;

            // Carry source time with PCM so a correction takes effect when that
            // boundary is heard, not while older buffered audio is still playing.
            this.times[slot] = packetTime + frame * 1e6 / 48000;
        }
        this.lastEnd = start + frames;
        this.endTime = packetTime + frames * 1e6 / 48000;
        const error = this.lastEnd - this.read - this.target - frames / 2;
        this.occupancy = this.occupancy * 0.98 + error * 0.02;

        // Correct independent capture/playback oscillator drift, without accumulating a queue.
        this.rate = 1 + Math.max(-0.003, Math.min(0.003, this.occupancy / 48000 * 0.1));
    }

    /** 再生済みの音声に対応する送信元の時刻を計算します。
     * @returns 呼出し元が使用する値です。
     */
    sourceTime()
    {
        const position = Math.floor(this.read), slot = ((position % this.capacity) + this.capacity) % this.capacity;
        return this.tags[slot] === position ? this.times[slot] + (this.read - position) * 1e6 / 48000 : this.endTime;
    }

    /** 出力要求に対応する音声を供給し、再生位置を更新します。
     * @returns 呼出し元が使用する値です。
     */
    process(inputs, outputs)
    {
        const left = outputs[0]?.[0], right = outputs[0]?.[1];
        if (!left || !right)
        {
            return true;
        }
        if (this.origin === null)
        {
            left.fill(0);
            right.fill(0);
            return true;
        }
        const wasBuffering = this.buffering;
        if (this.buffering && (this.target) <= (this.lastEnd - this.read))
        {
            this.buffering = false;
        }
        if ((this.blocks++ & 15) === 0 || wasBuffering !== this.buffering)
        {
            this.port.postMessage({ source: this.sourceTime(), context: currentTime, rate: this.buffering ? 0 : this.rate });
        }
        if (this.buffering)
        {
            left.fill(0);
            right.fill(0);
            return true;
        }
        const step = 48000 / sampleRate * this.rate;
        for (let index = 0; index < left.length; index++)
        {
            const position = Math.floor(this.read), fraction = this.read - position;
            const slot = ((position % this.capacity) + this.capacity) % this.capacity, next = (slot + 1) % this.capacity;
            left[index] = right[index] = 0;
            if ((0) <= (position) && this.tags[slot] === position)
            {
                const l = this.pcm[slot * 2], r = this.pcm[slot * 2 + 1];
                left[index] = l;
                right[index] = r;
                if (this.tags[next] === position + 1)
                {
                    left[index] += (this.pcm[next * 2] - l) * fraction;
                    right[index] += (this.pcm[next * 2 + 1] - r) * fraction;
                }
            }
            else
            {
                // Hold the source position through an underrun and rebuffer.
                // Advancing across missing PCM would repeatedly discard late audio.
                this.buffering = true;
                left.fill(0, index);
                right.fill(0, index);
                this.port.postMessage({ source: this.sourceTime(), context: currentTime + index / sampleRate, rate: 0 });
                break;
            }
            this.read += step;
        }
        return true;
    }
}

registerProcessor('contents-desktop-audio', DesktopAudioProcessor);
