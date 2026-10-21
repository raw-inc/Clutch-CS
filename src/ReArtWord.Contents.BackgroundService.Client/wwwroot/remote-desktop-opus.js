const configuration = { codec: 'opus', sampleRate: 48000, numberOfChannels: 2 };

/** このブラウザーでOpus音声を扱えるか確認します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function supportsDesktopOpus()
{
    if (typeof AudioDecoder !== 'function' || typeof EncodedAudioChunk !== 'function')
    {
        return false;
    }
    try
    {
        return (await AudioDecoder.isConfigSupported(configuration)).supported;
    }
    catch
    {
        return false;
    }
}

// Each message is one raw Opus packet, not an incrementally downloaded WAV/Ogg
// file. Reset discards old codec work and resumes at the next arriving packet.
/** Opusの復号処理と音声出力を構成します。
 * @returns receive・reset・closeを含む結果です。
 */
export function createDesktopOpus(output, failure)
{
    let decoder, epoch = 0, closed = false, pending = 0, latest = null, boundary = true;
    let draining = false, timer = 0, held = [];
    const portions = [];

    /** 一時状態を初期値へ戻します。 */
    function reset()
    {
        const expectedClosed = 'closed';

        epoch++;
        if (decoder && decoder.state !== expectedClosed)
        {
            decoder.close();
        }
        decoder = null;
        pending = 0;
        latest = null;
        boundary = true;
        portions.length = 0;
        clearTimeout(timer);
        timer = 0;
        draining = false;
        held = [];
    }

    /** 開始前に必要な資源と状態を準備します。 */
    function initialize()
    {
        const run = epoch;
        decoder = new AudioDecoder({
            /** 処理済みのデータを出力先へ渡します。 */
            output(data)
            {
                try
                {
                    if (closed || epoch !== run)
                    {
                        return;
                    }
                    pending = Math.max(0, pending - 1);

                    // Raw Opus produces one ordered PCM block per packet. Browsers may
                    // regenerate timestamps from decoded duration, before our lookahead trim.
                    const portion = portions.shift();
                    if (!portion || data.sampleRate !== 48000 || data.numberOfChannels !== 2 || data.numberOfFrames !== 960)
                    {
                        throw new Error('Invalid decoded desktop audio.');
                    }
                    const buffer = new ArrayBuffer(24 + portion.frames * 8), header = new DataView(buffer);
                    header.setInt32(0, 5);
                    header.setInt32(8, buffer.byteLength - 24);
                    header.setInt32(12, boundary ? 1 : 0);
                    header.setBigInt64(16, BigInt(Math.max(0, Math.round(portion.timestamp))));
                    boundary = false;
                    data.copyTo(new Float32Array(buffer, 24), { planeIndex: 0, format: 'f32', frameOffset: portion.skip, frameCount: portion.frames });
                    output(buffer, portion.ended);
                }
                catch (error)
                {
                    failure(error);
                }
                finally
                {
                    data.close();
                }
            },

            /** 処理の失敗を所有者へ通知します。 */
            error(error)
            {
                if (!closed && epoch === run)
                {
                    failure(error);
                }
            } });
        decoder.configure(configuration);
    }

    /** 受信データを検証し、現在の状態へ取り込みます。 */
    function receive(buffer)
    {
        if (closed)
        {
            return;
        }
        const header = new DataView(buffer), timestamp = Number(header.getBigInt64(16));
        validatePacketHeader(header, buffer.byteLength, timestamp);
        const frames = header.getInt32(24), skip = header.getInt32(28);
        const flags = header.getInt32(12);
        if (flags < 0 || (3) < (flags))
        {
            throw new Error('Invalid desktop Opus flags.');
        }
        if (frames < 1 || skip < 0 || (960) < (frames + skip))
        {
            throw new Error('Invalid desktop Opus sample range.');
        }
        if (draining)
        {
            held.push(buffer);
            if ((16) <= (held.length + pending))
            {
                reset();
                receive(buffer);
            }
            return;
        }
        const changed = latest != null && (flags & 1 || (250000) < (Math.abs(timestamp - latest)));
        if (changed && pending && pending < 16)
        {
            // Deliver the already submitted tail before starting the next
            // encoder epoch. Visibility reset/backpressure still drops stale work.
            draining = true;
            held.push(buffer);
            const run = epoch;
            timer = setTimeout(() =>
            {
                if (!closed && epoch === run)
                {
                    failure(new Error('Desktop audio decoder did not drain.'));
                }
            }, 1500);
            decoder.flush().then(() =>
            {
                if (closed || epoch !== run)
                {
                    return;
                }
                const next = held;
                reset();
                for (const packet of next)
                {
                    receive(packet);
                }
            }).catch(error =>
            {
                if (!closed && epoch === run)
                {
                    failure(error);
                }
            });
            return;
        }
        if ((16) <= (pending) || changed)
        {
            reset();
        }
        if (!decoder)
        {
            initialize();
        }
        latest = timestamp;
        pending++;
        portions.push({ timestamp, frames, skip, ended: !!(flags & 2) });
        decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp, data: new Uint8Array(buffer, 32) }));
    }

    return {
        receive,
        reset,

        /** 対象を閉じ、関連する状態を整理します。 */
        close()
        {
            if (!closed)
            {
                reset();
                closed = true;
            }
        }
    };
}

/** Opusパケットの種別・長さ・時刻が固定の通信契約に合うか検証します。 */
function validatePacketHeader(header, length, timestamp)
{
    if (header.getInt32(0) !== 10 || length < 33 || 1307 < length || header.getInt32(8) !== length - 24 || timestamp < 0)
    {
        throw new Error('Invalid desktop Opus packet.');
    }
}
