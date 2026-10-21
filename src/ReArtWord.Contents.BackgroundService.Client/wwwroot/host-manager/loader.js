import { node, empty } from './dom.js';

// Each read owns its generation. A late response never replaces a newer scope or a disposed view.
/** 照会の世代と取消しを管理する読取り処理を構成します。
 * @returns load・states・disposeを含む結果です。
 */
export function createReader(ctx, hosts, query, changed)
{
    let generation = 0, disposed = false, controller = null;
    const states = new Map();

    /** 対象の情報を取得し、現在の表示へ反映します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function loadAsync()
    {
        controller?.abort();
        controller = new AbortController();
        const current = ++generation;
        const signal = AbortSignal.any([ctx.signal, controller.signal]);
        states.clear();
        hosts.forEach(host => states.set(host.target.hostId, { host, loading: true }));
        changed(states);
        let next = 0;
        await Promise.all(Array.from({ length: Math.min(3, hosts.length) }, async () =>
        {
            while (next < hosts.length && !signal.aborted)
            {
                const host = hosts[next++];
                let state;
                try
                {
                    state = { host, snapshot: await ctx.read(host, query(host), signal) };
                }
                catch (error)
                {
                    if (signal.aborted)
                    {
                        return;
                    }
                    state = { host, error: error.message };
                }
                if (disposed || current !== generation)
                {
                    return;
                }
                states.set(host.target.hostId, state);
                changed(states);
            }
        }));
    }

    return { load: loadAsync, states,

        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {
            disposed = true;
            generation++;
            controller?.abort();
        } };
}

/** 照会状態を画面表示用の状態に変換します。 */
export function readStatus(root, ctx, states)
{
    root.replaceChildren();
    const entries = [...states.values()], pending = entries.filter(state => state.loading);
    root.hidden = !pending.length && !entries.some(state => state.error);
    if (pending.length)
    {
        const line = node('div', '', 'hm-loading');
        line.setAttribute('role', 'status');
        line.append(node('span', '', 'hm-spinner'), node('span', `${ctx.t('Loading settings')} · ${entries.length - pending.length} / ${entries.length}`));
        root.append(line);
    }
    for (const state of entries.filter(state => state.error))
    {
        const error = node('div', '', 'hm-read-error');
        error.append(node('strong', state.host.target.hostId), node('span', ctx.t(state.error)));
        root.append(error);
    }
}

/** 操作対象が選択されていない状態を表示します。
 * @returns disposeを含む結果です。
 */
export function noSelection(root, ctx)
{
    root.append(empty(ctx.t('Choose a host to get started'), ctx.t('Select one host, or select several to manage them together.'), 'monitor'));
    return {
        /** 所有する監視・接続・表示資源を解放します。 */
        dispose()
        {} };
}
