import { setControlLabel } from './ui-icons.js';
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
    selection.setAttribute('aria-label', ctx.t('Capture hosts and interfaces', '取得元のホストとインターフェース'));
    results.className = 'scope-results';
    let disposed = false, pendingView = 0, disposeResolve = null;
    toolbar.className = 'scope-toolbar';
    search.type = 'search';
    search.className = 'scope-search';
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

    const emptyPackets = document.createElement('p');
    emptyPackets.className = 'scope-empty';
    emptyPackets.textContent = ctx.t('Select a source and start capturing packets.', '取得元を選び、取得開始を押してください。');
    const sources = scopeSources(selection, ctx, send, updateControls);
    const start = ctx.button(ctx.t('Start', '取得開始'), () =>
    {
        ctx.say('');
        send({ type: 'start', sources: sources.selected() });
        sources.pending();
    });
    const stop = ctx.button(ctx.t('Stop', '取得停止'), () => send({ type: 'stop' }));
    const clear = ctx.button(ctx.t('Clear captured packets', '取得したパケットを消去'), () =>
    {
        send({ type: 'clear' });
        table.clear();
    });
    const latest = ctx.button(ctx.t('Follow latest', '最新に追従'), () => table.latest(!table.following));
    const reload = ctx.button(ctx.t('Refresh hosts', 'ホスト一覧を更新'), () => sources.loadAsync());
    setControlLabel(reload, ctx.icon('refresh'));
    display.setAttribute('aria-label', ctx.t('Endpoint display', '送受信先の表示'));
    display.append(new Option(ctx.t('IP:Port', 'IPアドレス:ポート番号'), 'ip'), new Option(ctx.t('Host:Port', 'ホスト名:ポート番号'), 'host'));
    start.className = 'primary';
    const filters = document.createElement('div');
    filters.className = 'scope-filters';
    const filterLabel = document.createElement('label');
    filterLabel.textContent = ctx.t('Display filter', '表示フィルター');
    filterLabel.append(search);
    const captureState = document.createElement('span');
    captureState.className = 'badge';
    captureState.setAttribute('role', 'status');
    toolbar.append(captureState);
    const columns = document.createElement('label'), showColumns = document.createElement('input');
    columns.className = 'scope-column-choice';
    showColumns.type = 'checkbox';
    showColumns.onchange = () => container.classList.toggle('all-columns', showColumns.checked);
    columns.append(showColumns, document.createTextNode(ctx.t('All columns', 'すべての列')));
    filters.append(filterLabel, latest, display, columns);
    const sample = document.createElement('p');
    sample.className = 'scope-filter-example';
    sample.textContent = ctx.t('Example: OSC AND "/stage"', '入力例：OSC AND "/stage"');
    setControlLabel(clear, ctx.icon('trash'));
    clear.disabled = true;
    const packetListHeading = document.createElement('div'), packetListTitle = document.createElement('h2'), scrollHint = document.createElement('span');
    packetListHeading.className = 'scope-packet-heading';
    packetListTitle.textContent = ctx.t('Captured packets', '取得したパケット');
    scrollHint.className = 'scope-scroll-hint';
    scrollHint.textContent = ctx.t('Scroll the list to see more.', '続きは一覧をスクロールして表示');
    packetListHeading.append(packetListTitle, clear, scrollHint);
    toolbar.append(start, stop);
    selection.prepend(reload);
    results.append(filters, sample, searchError, packetListHeading, status, emptyPackets);
    workspace.append(selection, results);
    selection.prepend(toolbar);
    container.append(workspace);

    /** 統計の項目を作成します。
     * @returns 表示値を更新するテキストノードです。
     */
    function stat(label)
    {
        const item = document.createElement('span'), value = document.createTextNode('');
        item.append(label + ': ', value);
        status.append(item, ' ');
        return value;
    }

    const matched = stat(ctx.t('Matched', '一致'));
    const received = stat(ctx.t('Observations', '受信'));
    const merged = stat(ctx.t('Merged', '重複統合'));
    const rolled = stat(ctx.t('Rolled out', '保持上限除外'));
    const captureDrops = stat(ctx.t('Capture drops', '取得欠落'));
    const deliveryDrops = stat(ctx.t('Delivery drops', '配送省略'));
    const rowsSize = stat(ctx.t('Rows', '一覧'));
    const contentsSize = stat(ctx.t('Contents', '原本'));
    stat(ctx.t('Time zone', '時刻')).textContent = ctx.timeZone ?? 'UTC';
    const table = scopeTable(results, ctx, send, (view, following) =>
    {
        latest.setAttribute('aria-pressed', String(following));
        emptyPackets.hidden = (0) < (view.retained) || sources.running;
        status.classList.toggle('warn', (0) < (view.backendDropped) || (0) < (view.deliveryDropped));
        clear.disabled = view.retained === 0;
        matched.textContent = view.total.toLocaleString() + ' / ' + view.retained.toLocaleString();
        received.textContent = view.received.toLocaleString();
        merged.textContent = view.duplicates.toLocaleString();
        rolled.textContent = view.rolled.toLocaleString();
        captureDrops.textContent = view.backendDropped.toLocaleString();
        deliveryDrops.textContent = view.deliveryDropped.toLocaleString();
        rowsSize.textContent = (view.bytes / 1048576).toFixed(1) + ' MiB';
        contentsSize.textContent = (view.sourceBytes / 1048576).toFixed(1) + ' MiB';
    });
    search.addEventListener('input', () => send({ type: 'search', query: search.value }), { signal: ctx.signal });
    display.onchange = () => table.names(display.value === 'host');

    /** 未選択・応答待ち・取得中を区別します。 */
    function updateControls()
    {
        start.disabled = sources.running || !sources.selected().length;
        stop.disabled = !sources.running;
        reload.disabled = sources.running;
        if (sources.running)
        {
            emptyPackets.hidden = true;
        }
        captureState.textContent = sources.running ? ctx.t('Capturing', '取得中') : ctx.t('Stopped', '停止中');
        captureState.classList.toggle('good', sources.running);
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
        searchSummary.textContent = ctx.t('Advanced Search', '詳細検索');
        query.type = 'search';
        query.className = 'scope-search';
        query.setAttribute('aria-label', searchSummary.textContent);
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
