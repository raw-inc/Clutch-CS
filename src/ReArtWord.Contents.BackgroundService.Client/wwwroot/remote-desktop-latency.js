/** 端末間の時計差を往復時刻で補正し、Core送信開始から映像受信完了までを見積もります。
 * @returns 時計照会と映像ごとの測定を管理する、一接続だけの状態です。
 */
export function createDesktopLatency()
{
    let sequence = 0, pending = null, nextProbe = 0, samples = [], active = false;
    const markers = new Map(), delays = new Map();

    /** 時刻の範囲と整数表現を確認します。
     * @returns 通信時刻として使用できるかです。
     */
    function validTime(value)
    {
        return Number.isSafeInteger(value) && 0 <= value;
    }

    /** 一接続の未確定な時計差と映像測定を破棄します。 */
    function reset()
    {
        pending = null;
        nextProbe = 0;
        samples = [];
        markers.clear();
        delays.clear();
        active = false;
    }

    return {
        reset,

        /** 両端の対応確認後だけ、拡張パケットを使用します。 */
        enable(value)
        {
            reset();
            active = value;
        },

        /** 未応答を無制限に積まず、単調時計で新しい一回の照会を準備します。
         * @returns 入力パケットの連番と送信時刻、またはnullです。
         */
        probe(now)
        {
            if (!active || now < nextProbe || pending && now - pending.at < 5000)
            {
                return null;
            }
            const sent = Math.floor(now * 1000);
            if (!validTime(sent))
            {
                return null;
            }
            sequence = sequence % 2147483647 + 1;
            pending = { sequence, sent, at: now };
            nextProbe = now + 1000;
            return [sequence, Math.floor(sent / 4294967296), sent % 4294967296];
        },

        /** 自分の照会だけを受理し、Core内の待機を往復時間から差し引きます。 */
        reply(id, sent, received, replied, now)
        {
            const arrived = Math.floor(now * 1000);
            if (!active || !pending || pending.sequence !== id || pending.sent !== sent || ![sent, received, replied, arrived].every(validTime))
            {
                return;
            }
            pending = null;
            const processing = replied - received, elapsed = arrived - sent, roundTrip = elapsed - processing;
            if (processing < 0 || roundTrip < 0 || 10000000 < elapsed)
            {
                return;
            }
            samples = samples.filter(sample => now - sample.at <= 60000).slice(-15);
            samples.push({ at: now, roundTrip, offset: ((received - sent) + (replied - arrived)) / 2 });
        },

        /** 次の映像と対応する送信時刻を画面ごとに一つだけ保持します。 */
        mark(monitor, captured, sent)
        {
            if (active && Number.isInteger(monitor) && 0 <= monitor && monitor < 32 && validTime(captured) && validTime(sent))
            {
                markers.set(monitor, { captured, sent });
            }
        },

        /** 映像全体の受信完了時刻を、同じフレームの送信開始時刻と比較します。 */
        frame(monitor, captured, now)
        {
            const marker = markers.get(monitor);
            markers.delete(monitor);
            delays.delete(monitor);
            if (!marker || marker.captured !== captured)
            {
                return;
            }
            const fresh = samples.filter(sample => now - sample.at <= 60000);
            const best = fresh.reduce((value, sample) => !value || sample.roundTrip < value.roundTrip ? sample : value, null);
            if (!best)
            {
                return;
            }
            const elapsed = now * 1000 - marker.sent + best.offset;
            if (elapsed < -best.roundTrip / 2)
            {
                return;
            }
            delays.set(monitor, { milliseconds: Math.max(0, elapsed / 1000), calibratedAt: best.at });
        },

        /** 参照中の画面だけの直近値を表示し、未対応・未校正をゼロと扱いません。
         * @returns 画面ごとの最大推定ミリ秒、またはnullです。
         */
        value(monitors, now)
        {
            const values = monitors.map(id => delays.get(id)).filter(value => value && now - value.calibratedAt <= 60000);
            return values.length ? Math.max(...values.map(value => value.milliseconds)) : null;
        },
    };
}
