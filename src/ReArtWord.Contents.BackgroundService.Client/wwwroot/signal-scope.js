import { scopeSources } from './signal-scope-sources.js';
import { scopeTable } from './signal-scope-table.js';
import { experimentalEnabled } from './experimental.js';

/** キャプチャの操作・描画だけを所有し、保持と検索はWorkerへ渡します。
 * @returns 画面終了時に取得接続を回収する操作です。
 */
export async function signalScopeViewAsync(container, ctx)
{
    container.className = 'signal-scope';
    const worker = new Worker(new URL('./signal-scope-worker.js', import.meta.url), { type: 'module' });
    const toolbar = document.createElement('div'), search = document.createElement('input'), searchError = document.createElement('p');
    const status = document.createElement('p'), selection = document.createElement('aside'), display = document.createElement('select'), advancedStatus = document.createElement('p');
    const workspace = document.createElement('div'), results = document.createElement('section');
    workspace.className = 'scope-workspace';
    selection.className = 'scope-sidebar';
    selection.setAttribute('aria-label', ctx.t('Capture hosts and interfaces', '取得するHostとインターフェース'));
    results.className = 'scope-results';
    let disposed = false, pendingView = 0, disposeResolve = null;
    toolbar.className = 'scope-toolbar';
    search.type = 'search';
    search.className = 'scope-search';
    search.placeholder = 'OSC AND (192.168.10. OR 192.168.20.) AND "/address/to"';
    search.setAttribute('aria-label', ctx.t('Search captured packets', '取得したパケットを検索'));
    search.title = ctx.t('Search table information locally: AND / OR, parentheses and quoted text. Quoted "and" and "or" are literal text.', '一覧情報をブラウザ内で検索します。AND / OR・括弧・引用符に対応。引用符内の"and"や"or"は文字列です。');
    searchError.className = 'scope-search-error';
    searchError.setAttribute('role', 'status');
    status.className = 'scope-status';
    status.setAttribute('role', 'status');

    /** 終了後の要求を残しません。 */
    function send(message)
    {
        if (!disposed)
        {
            worker.postMessage(message);
        }
    }

    /** 描画要求を一フレームにまとめます。 */
    function refresh()
    {
        if (!pendingView && !disposed)
        {
            pendingView = requestAnimationFrame(() =>
            {
                pendingView = 0;
                table.refresh();
            });
        }
    }

    const sources = scopeSources(selection, ctx, send, updateControls);
    const start = ctx.button(ctx.t('Start', 'Start'), () =>
    {
        ctx.say('');
        send({ type: 'start', sources: sources.selected() });
        sources.pending();
    });
    const stop = ctx.button(ctx.t('Stop', 'Stop'), () => send({ type: 'stop' }));
    const clear = ctx.button(ctx.t('Clear', 'クリア'), () =>
    {
        send({ type: 'clear' });
        table.clear();
    });
    const latest = ctx.button(ctx.t('Follow latest', '最新に追従'), () => table.latest());
    const reload = ctx.button(ctx.t('Refresh hosts', 'Host再取得'), () => sources.loadAsync());
    display.setAttribute('aria-label', ctx.t('Endpoint display', '送受信先の表示'));
    display.append(new Option('IP:Port', 'ip'), new Option('Host:Port', 'host'));
    toolbar.append(search, start, stop, clear, latest, display);
    selection.prepend(reload);
    results.append(status);
    workspace.append(selection, results);
    container.append(toolbar, searchError, workspace);
    const table = scopeTable(results, ctx, send, view =>
    {
        status.textContent = ctx.t('Matched', '一致') + ': ' + view.total.toLocaleString() + ' / ' + view.retained.toLocaleString() + ' · '
            + ctx.t('Observations', '受信') + ': ' + view.received.toLocaleString() + ' · ' + ctx.t('Merged', '重複統合') + ': ' + view.duplicates.toLocaleString() + ' · '
            + ctx.t('Rolled out', '保持上限除外') + ': ' + view.rolled.toLocaleString() + ' · ' + ctx.t('Capture drops', '取得欠落') + ': ' + view.backendDropped.toLocaleString()
            + ' · ' + ctx.t('Delivery drops', '配送省略') + ': ' + view.deliveryDropped.toLocaleString()
            + ' · ' + ctx.t('Rows', '一覧') + ': ' + (view.bytes / 1048576).toFixed(1) + ' MiB · '
            + ctx.t('Contents', '原本') + ': ' + (view.sourceBytes / 1048576).toFixed(1) + ' MiB · ' + (ctx.timeZone ?? 'UTC');
    });
    search.addEventListener('input', () => send({ type: 'search', query: search.value }), { signal: ctx.signal });
    display.onchange = () => table.names(display.value === 'host');

    /** 未選択・応答待ち・取得中を区別します。 */
    function updateControls()
    {
        start.disabled = sources.running || !sources.selected().length;
        stop.disabled = !sources.running;
        reload.disabled = sources.running;
        container.classList.toggle('capturing', sources.running);
    }

    /** 通常画面を複雑にせず、保持量をセッション内で調整します。 */
    function advanced()
    {
        if (!experimentalEnabled(true))
        {
            return;
        }
        const panel = document.createElement('details'), title = document.createElement('summary'), fields = [];
        const searchPanel = document.createElement('details'), searchSummary = document.createElement('summary'), query = document.createElement('input'), mode = document.createElement('select');
        searchPanel.className = 'scope-advanced';
        searchSummary.textContent = 'Advanced Search';
        query.type = 'search';
        query.className = 'scope-search';
        query.setAttribute('aria-label', 'Advanced Search');
        query.placeholder = ctx.t('Search retained details and packet contents at the source', '保持中の詳細・パケット本文を取得側で検索');
        mode.setAttribute('aria-label', ctx.t('Advanced search mode', '高度検索の方式'));
        mode.append(new Option('Text', 'text'), new Option('Hex', 'hex'));
        advancedStatus.className = 'scope-search-error';
        advancedStatus.setAttribute('role', 'status');
        const advancedToolbar = document.createElement('div');
        advancedToolbar.className = 'scope-toolbar';

        /** 高度検索は通常検索を変更せず、取得側へ追加条件を渡します。 */
        function updateAdvanced()
        {
            advancedStatus.textContent = ctx.t('Searching retained packet contents…', '保持中のパケットを取得側で検索しています…');
            send({ type: 'advanced', query: query.value, mode: mode.value });
        }

        query.addEventListener('input', updateAdvanced, { signal: ctx.signal });
        mode.addEventListener('change', updateAdvanced, { signal: ctx.signal });
        advancedToolbar.append(mode, query);
        searchPanel.append(searchSummary, advancedToolbar, advancedStatus);
        panel.className = 'scope-limits';
        title.textContent = ctx.t('Capture limits', '保持・重複照合の設定');
        panel.append(title);
        for (const [name, value, minimum, maximum] of [[ctx.t('Packets', '保持件数'), 50000, 100, 250000], [ctx.t('Memory (MiB)', '概算容量 (MiB)'), 320, 1, 1024], [ctx.t('Duplicate window (ms)', '重複照合幅 (ms)'), 250, 0, 2000]])
        {
            const label = document.createElement('label'), input = document.createElement('input');
            input.type = 'number';
            input.min = minimum;
            input.max = maximum;
            input.step = 1;
            input.value = value;
            label.append(document.createTextNode(name), input);
            fields.push(input);
            panel.append(label);
        }
        panel.append(ctx.button(ctx.t('Apply limits', '上限を適用'), () =>
        {
            if (fields.every(input => input.reportValidity()))
            {
                send({ type: 'configure', limit: Number(fields[0].value), maximumBytes: Number(fields[1].value) * 1048576, duplicateWindow: Number(fields[2].value) });
            }
        }));
        selection.append(searchPanel, panel);
    }

    worker.onmessage = event =>
    {
        const message = event.data;
        switch (message.type)
        {
            case 'catalog': sources.catalog(message);
                break;
            case 'state': sources.state(message);
                break;
            case 'view': table.render(message);
                break;
            case 'detail': table.detail(message);
                break;
            case 'changed': refresh();
                break;
            case 'search':
                searchError.textContent = message.error ?? '';
                search.setAttribute('aria-invalid', String(Boolean(message.error)));
                refresh();
                break;
            case 'advanced':
                advancedStatus.textContent = message.error ?? (message.pending ? ctx.t('Searching retained packet contents…', '保持中のパケットを取得側で検索しています…') : '');
                refresh();
                break;
            case 'error': ctx.say((message.host ? message.host + ' · ' : '') + message.message);
                break;
            case 'disposed': disposeResolve?.();
                break;
        }
    };
    worker.onerror = () =>
    {
        worker.terminate();
        disposed = true;
        for (const input of container.querySelectorAll('button,input,select'))
        {
            input.disabled = true;
        }
        ctx.say(ctx.t('Capture processing stopped. Reopen this page to connect again.', 'キャプチャの表示処理が停止しました。再接続するには画面を開き直してください。'));
    };
    advanced();
    send({ type: 'time-zone', timeZone: ctx.timeZone ?? 'UTC' });
    updateControls();
    try
    {
        await sources.loadAsync();
    }
    catch (error)
    {
        if (!ctx.signal.aborted)
        {
            ctx.say(error.message);
        }
    }
    refresh();
    return { updateControls,

        /** 画面の離脱時にWebSocketを閉じ、Coreのキャプチャを終了させます。
         * @returns Workerの解放です。
         * @contract 画面所有者が呼ぶ共通のdisposeメソッド名を維持します。
         */
        async dispose()
        {
            cancelAnimationFrame(pendingView);
            pendingView = 0;
            table.dispose();
            if (disposed)
            {
                worker.terminate();
                return;
            }
            await new Promise(resolve =>
            {
                const timeout = setTimeout(resolve, 1000);
                disposeResolve = () =>
                {
                    clearTimeout(timeout);
                    resolve();
                };
                send({ type: 'dispose' });
                disposed = true;
            });
            worker.terminate();
        } };
}
