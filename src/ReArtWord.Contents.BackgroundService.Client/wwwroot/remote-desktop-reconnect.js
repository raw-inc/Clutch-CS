/** カンマ区切りの待機時間を有限の再試行列へ変換します。
 * @returns 各失敗の確定時点から待つミリ秒です。
 */
export function parseDesktopReconnectDelays(text)
{
    const parts = text.split(',').map(value => value.trim());
    if (!parts.length || (20) < (parts.length) || parts.some(value => !/^\d+$/u.test(value) || (300000) < (Number(value))))
    {
        throw new Error('Enter 1–20 comma-separated delays from 0 to 300000 milliseconds.');
    }
    return parts.map(Number);
}

/** 一画面の接続復帰だけを所有し、失敗が確定した時点から次の待機を始めます。
 * @returns 有効化、失敗、成功、取消しの境界です。
 */
export function createDesktopReconnect(connectAsync, changed, error)
{
    let enabled = false, delays = [0, 3000, 10000], attempt = 0, timer = null, trying = false, revision = 0;

    /** 現在の待機と試行を画面へ通知します。 */
    function publish()
    {
        changed({ enabled, attempt, maximum: delays.length, waiting: timer !== null, trying, pending: timer !== null || trying, delay: delays[attempt - 1] ?? 0 });
    }

    /** 古い予約と開始途中の結果が、新しい接続の状態を上書きしないようにします。 */
    function cancel()
    {
        revision++;
        clearTimeout(timer);
        timer = null;
        trying = false;
        attempt = 0;
        publish();
    }

    return {
        /** 画面を離れると破棄する復帰設定を切り替えます。 */
        enable(value)
        {
            enabled = value;
            if (!enabled)
            {
                cancel();
                return;
            }
            publish();
        },

        /** 確定済みの待機設定を次回の切断へ適用します。 */
        configure(value)
        {
            delays = parseDesktopReconnectDelays(value);
            cancel();
        },

        /** 接続失敗を受け、残っている次の一回だけを予約します。
         * @returns 再試行を予約したかです。
         */
        failed()
        {
            if (timer !== null)
            {
                return true;
            }
            trying = false;
            if (!enabled || delays.length <= attempt)
            {
                publish();
                return false;
            }
            const delay = delays[attempt++], current = ++revision;
            timer = setTimeout(async () =>
            {
                if (current !== revision)
                {
                    return;
                }
                timer = null;
                trying = true;
                publish();
                try
                {
                    await connectAsync();
                }
                catch (failure)
                {
                    if (current === revision)
                    {
                        cancel();
                        error(failure);
                    }
                }
            }, delay);
            publish();
            return true;
        },

        connected: cancel,
        cancel,
    };
}

/** WebSocketの終了通知から、確認できた範囲だけを説明します。
 * @returns 翻訳する説明と、自動復帰の対象かです。
 */
export function desktopDisconnectReason(event)
{
    const known = {
        'Desktop.HostUnavailable': ['The selected host is currently unavailable.', '選択したホストへ現在接続できません。'],
        'Desktop.SessionUnavailable': ['The Core could not start a desktop session.', 'Coreがデスクトップセッションを開始できませんでした。'],
        'Desktop.UpstreamDisconnected': ['The connection between ClientHost and Core was interrupted.', 'ClientHostとCore間の通信が切断されました。'],
        'Desktop.InvalidData': ['The desktop connection received invalid data.', 'リモートデスクトップの通信形式を確認できませんでした。'],
    };
    const fallback = event.code === 1006
        ? ['The network connection ended without a close notification. The cause could not be confirmed.', '通信が終了通知なしで切断されました。切断原因は確認できませんでした。']
        : ['The desktop connection ended.', 'リモートデスクトップの接続が終了しました。'];
    return { message: known[event.reason] ?? fallback, retryable: [1001, 1005, 1006, 1011, 1012, 1013].includes(event.code) };
}
