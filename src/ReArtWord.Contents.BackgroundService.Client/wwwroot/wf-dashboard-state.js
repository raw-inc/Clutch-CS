/** 表示対象を一度送信し、その後はState変更だけを受信します。
 * @returns 画面離脱時に接続と再試行を解放する操作です。
 */
export function watch(callback, generation, cluster, peer, instance, targets)
{
    let socket = null, retry = 0, attempt = 0, disposed = false, delivery = Promise.resolve();
    const pending = new Map();
    let sending = false, problem = null;

    /** 遅い描画へ同じカードの値を積み上げず、最新だけを反映します。 */
    function publish()
    {
        if (disposed || sending)
        {
            return;
        }
        sending = true;
        const batch = [...pending.values()], observedProblem = problem;
        pending.clear();
        delivery = callback.invokeMethodAsync('ReceiveAsync', generation, batch, observedProblem).catch(() =>
        {}).finally(() =>
        {
            sending = false;
            if (!disposed && (pending.size || problem !== observedProblem))
            {
                publish();
            }
        });
    }

    /** 再接続時は初期値を取得し直し、通信断を不存在と表示しません。 */
    function connect()
    {
        if (disposed)
        {
            return;
        }
        const url = new URL('/client/state-stream', location.href), secure = 'https:';
        url.protocol = url.protocol === secure ? 'wss:' : 'ws:';
        url.search = new URLSearchParams({ cluster, peer, instance });
        const current = new WebSocket(url);
        socket = current;
        current.onopen = () => current.send(JSON.stringify(targets));
        current.onmessage = event =>
        {
            if (disposed || current !== socket)
            {
                return;
            }
            try
            {
                const value = JSON.parse(event.data);
                if (Array.isArray(value))
                {
                    attempt = 0;
                    problem = null;
                    for (const item of value)
                    {
                        if (targets.some(target => target.id === item.id))
                        {
                            pending.set(item.id, item);
                        }
                    }
                }
                else
                {
                    problem = value.problem ?? 'State observation disconnected';
                }
                publish();
            }
            catch
            {
                current.close();
            }
        };
        current.onclose = () =>
        {
            if (!disposed && current === socket)
            {
                problem = 'State observation disconnected';
                publish();
                retry = setTimeout(connect, Math.min(15000, 1000 * 2 ** Math.min(attempt++, 4)));
            }
        };
    }

    connect();
    return {
        /** 購読を閉じ、遅延応答が後続画面へ届くのを止めます。
         * @returns 処理中の描画通知の終了です。
         * @contract JSの資源解放契約としてdisposeを使用します。
         */
        async dispose()
        {
            disposed = true;
            clearTimeout(retry);
            socket?.close();
            pending.clear();
            await delivery;
        }
    };
}
