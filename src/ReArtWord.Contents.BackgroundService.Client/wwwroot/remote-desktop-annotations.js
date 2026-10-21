// Annotation packets never enter the OS input path. Stable sequence IDs retain
// their original local birth time when snapshots repeat or arrive late.
/** 受信した指示表示の寿命と座標を保持します。
 * @returns receive・read・clearを含む結果です。
 */
export function createAnnotationState()
{
    let points = new Map(), cursors = [], duration = 4000, lastReceived = -Infinity;
    return {
        /** 受信データを検証し、現在の状態へ取り込みます。 */
        receive(value, now)
        {
            /** 入力値が許容範囲の整数か検証します。
             * @returns 呼出し元が使用する値です。
             */
            const integer = value => Number.isSafeInteger(value);

            if (value?.duration !== 4000 || !Array.isArray(value.cursors) || (32) < (value.cursors.length) || !Array.isArray(value.points) || (512) < (value.points.length))
            {
                throw new Error('Invalid viewer annotations');
            }

            /** 指示表示に使用できる色の値かを判定します。
             * @returns 呼出し元が使用する判定結果です。
             */
            const validColor = color => integer(color) && (0) <= (color) && color <= 0xffffff;

            /** 指示座標が許容範囲の数値かを判定します。
             * @returns 呼出し元が使用する判定結果です。
             */
            const coordinate = number => integer(number) && Math.abs(number) <= 1000000;

            /** 受信した線の一点の範囲と寿命を検証します。
             * @returns 通信契約に合う一点であればtrueです。
             */
            function validPoint(row)
            {
                return Array.isArray(row) && row.length === 6 && row.every(integer) && 1 <= row[0] && validColor(row[1])
                    && 0 <= row[2] && row[2] <= 0x7fffffff && coordinate(row[3]) && coordinate(row[4]) && 0 <= row[5] && row[5] < value.duration;
            }

            if (value.cursors.some(row => !Array.isArray(row) || row.length !== 3 || !validColor(row[0]) || !coordinate(row[1]) || !coordinate(row[2])))
            {
                throw new Error('Invalid viewer cursor');
            }
            const next = new Map();
            for (const row of value.points)
            {
                if (!validPoint(row) || next.has(row[0]))
                {
                    throw new Error('Invalid viewer trail');
                }
                const previous = points.get(row[0]);
                next.set(row[0], { sequence: row[0], color: row[1], line: row[2], x: row[3], y: row[4], born: Math.min(previous?.born ?? Infinity, now - row[5]) });
            }
            points = next;
            cursors = value.cursors;
            duration = value.duration;
            lastReceived = now;
        },

        /** 保存された値または現在の応答を読み取ります。
         * @returns points・cursors・alphaを含む結果です。
         */
        read(now)
        {
            const visible = [...points.values()].filter(point => now - point.born < duration);
            return { points: visible, cursors: now - lastReceived < 2000 ? cursors : [],

                alpha: point => Math.max(0, 1 - (now - point.born) / duration) };
        },

        /** 保持している一時状態または表示を消去します。 */
        clear()
        {
            points.clear();
            cursors = [];
            lastReceived = -Infinity;
        }
    };
}

/** 指示の経過時間に応じてカーソルと線を描画します。
 * @returns 呼出し元が使用する値です。
 */
export function drawAnnotations(context, monitor, state, now)
{
    context.clearRect(0, 0, monitor.width, monitor.height);
    const { points, cursors, alpha } = state.read(now), previous = new Map();

    /** 色と透明度をcanvas用の表記へ変換します。
     * @returns 呼出し元が使用する値です。
     */
    const rgba = (color, opacity) => `rgba(${color >> 16 & 255},${color >> 8 & 255},${color & 255},${opacity})`;

    context.lineWidth = 5;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    for (const point of points)
    {
        const key = point.color + ':' + point.line, start = previous.get(key) ?? point;
        const x = start.x - monitor.left, y = start.y - monitor.top, endX = point.x - monitor.left, endY = point.y - monitor.top;
        if (x === endX && y === endY)
        {
            context.fillStyle = rgba(point.color, 0.59 * alpha(point));
            context.beginPath();
            context.arc(x, y, 2.5, 0, Math.PI * 2);
            context.fill();
        }
        else
        {
            const gradient = context.createLinearGradient(x, y, endX, endY);
            gradient.addColorStop(0, rgba(point.color, 0.59 * alpha(start)));
            gradient.addColorStop(1, rgba(point.color, 0.59 * alpha(point)));
            context.strokeStyle = gradient;
            context.beginPath();
            context.moveTo(x, y);
            context.lineTo(endX, endY);
            context.stroke();
        }
        previous.set(key, point);
    }
    for (const [color, globalX, globalY] of cursors)
    {
        const x = globalX - monitor.left, y = globalY - monitor.top;
        context.fillStyle = rgba(color, 0.65);
        context.beginPath();
        context.moveTo(x, y);
        context.lineTo(x, y + 23);
        context.lineTo(x + 5, y + 17);
        context.lineTo(x + 10, y + 24);
        context.lineTo(x + 14, y + 21);
        context.lineTo(x + 9, y + 15);
        context.lineTo(x + 16, y + 15);
        context.closePath();
        context.fill();
    }
    return points.length !== 0 || cursors.length !== 0;
}

/** 閲覧者の指示座標を固定の通信形式へ変換します。
 * @returns move・down・up・tick・leaveを含む結果です。
 */
export function createAnnotationPointer(send, locate, available)
{
    let pending = null, drawing = false, line = 0, monitor = -1, last = -Infinity, visible = false;

    /** 現在の値を受信側へ通知します。 */
    function publish(event, now, immediate = false)
    {
        if (!available())
        {
            return;
        }
        const position = locate(event);
        if (!position)
        {
            leave();
            return;
        }
        if (drawing && monitor !== position[0])
        {
            line = line % 0x7ffffffe + 1;
        }
        monitor = position[0];
        if (!immediate && now - last < 40)
        {
            pending = { clientX: event.clientX, clientY: event.clientY };
            return;
        }
        send(13, ...position, drawing ? 2 : 1, line);
        last = now;
        pending = null;
        visible = true;
    }

    /** 操作対象から離れた状態を通知します。 */
    function leave()
    {
        pending = null;
        drawing = false;
        monitor = -1;
        if (visible)
        {
            send(13);
        }
        visible = false;
    }

    return {
        /** ポインターの移動を進行中の操作へ反映します。 */
        move(event)
        {
            if (available())
            {
                pending = { clientX: event.clientX, clientY: event.clientY };
            }
        },

        /** ポインターの押下から操作状態を開始します。
         * @returns 呼出し元が使用する値です。
         */
        down(event, now)
        {
            if (!available() || event.button !== 0)
            {
                return false;
            }
            drawing = true;
            line = line % 0x7ffffffe + 1;
            publish(event, now, true);
            return true;
        },

        /** ポインターの解放時に操作を確定または終了します。 */
        up(event, now)
        {
            if (!drawing)
            {
                return;
            }
            publish(event, now, true);
            drawing = false;
            publish(event, now, true);
        },

        /** 経過時間に応じて状態と期限を更新します。 */
        tick(now)
        {
            if (pending)
            {
                publish(pending, now);
            }
        },

        leave
    };
}
