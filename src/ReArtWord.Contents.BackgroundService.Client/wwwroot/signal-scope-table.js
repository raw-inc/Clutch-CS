/** 固定行高の可視範囲だけを描画し、選択詳細は新着で消しません。
 * @returns Workerの一覧と詳細を表示する操作です。
 */
export function scopeTable(container, ctx, send, report)
{
    const height = 26, overscan = 6, viewport = document.createElement('div'), table = document.createElement('table');
    const header = document.createElement('thead'), labels = document.createElement('tr'), body = document.createElement('tbody');
    viewport.className = 'scope-list record-detail-scroll';
    viewport.tabIndex = 0;
    viewport.setAttribute('aria-label', ctx.t('Captured packets', '取得したパケット'));
    table.className = 'scope-packets';
    for (const label of ['#', ctx.t('Time', '時刻'), ctx.t('Protocol', 'プロトコル'), ctx.t('Source', '送信元'), ctx.t('Destination', '宛先'), ctx.t('Info', '内容'), ctx.t('Seen', '観測')])
    {
        const cell = document.createElement('th');
        cell.textContent = label;
        labels.append(cell);
    }
    header.append(labels);
    table.append(header, body);
    viewport.append(table);
    const panels = document.createElement('div'), description = document.createElement('pre'), hex = document.createElement('pre');
    panels.className = 'scope-details';
    panels.dataset.empty = 'true';
    const detailHint = ctx.t('Select a packet to view its contents.', 'パケットを選ぶと内容を表示します。');
    for (const [name, content] of [[ctx.t('Packet details', 'パケット詳細'), description], ['Hex / ASCII', hex]])
    {
        const section = document.createElement('section'), title = document.createElement('h2');
        title.textContent = name;
        const empty = document.createElement('p');
        empty.className = 'scope-detail-empty';
        empty.textContent = detailHint;
        section.append(title, empty, content);
        panels.append(section);
    }
    container.append(viewport, panels);
    let follow = true, hostNames = false, selected = null, request = 0, programmedScroll = -1, anchor = null;
    let firstRendered = 0, requestedOffset = 0, requestedAnchor = null;
    let format;
    try
    {
        format = new Intl.DateTimeFormat('en-GB', { timeZone: ctx.timeZone ?? 'UTC', hourCycle: 'h23', hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
    }
    catch
    {
        format = new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', hourCycle: 'h23', hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
    }

    /** スクロールのたびに保持データを再転送せず、表示範囲だけ要求します。 */
    function refresh()
    {
        const firstVisible = Math.floor(viewport.scrollTop / height);
        const start = Math.max(0, firstVisible - overscan);
        const count = Math.min(200, Math.ceil(viewport.clientHeight / height) + 2 * overscan);
        requestedAnchor = anchor;
        requestedOffset = Math.max(0, viewport.scrollTop - firstRendered * height);
        send({ type: 'view', request: ++request, start, anchor, count, follow });
    }

    /** ポートを常に残し、名前が解決できない場合はIPへ戻します。
     * @returns Source/Destinationの表示です。
     */
    function endpoint(address, name, port)
    {
        const value = hostNames ? name ?? address : address;
        return port === null || port === undefined ? value : (value?.includes(':') ? '[' + value + ']' : value) + ':' + port;
    }

    /** 非表示行の高さをテーブル内に確保します。
     * @returns 一つの余白行です。
     */
    function spacer(size)
    {
        const row = document.createElement('tr'), cell = document.createElement('td');
        cell.colSpan = 7;
        cell.className = 'scope-spacer';
        cell.style.height = Math.max(0, size) + 'px';
        row.append(cell);
        return row;
    }

    /** 新着のたびに一覧全件をDOMへ追加しません。 */
    function render(view)
    {
        if (view.request !== request)
        {
            return;
        }
        firstRendered = view.first;
        const fragment = document.createDocumentFragment();
        fragment.append(spacer(view.first * height));
        for (const packet of view.rows)
        {
            const row = document.createElement('tr');
            row.dataset.packet = packet.id;
            row.classList.toggle('selected', selected === packet.id);
            const values = [packet.id, format.format(new Date(packet.time)), packet.protocol, endpoint(packet.source, packet.sourceName, packet.sourcePort),
                endpoint(packet.destination, packet.destinationName, packet.destinationPort), [packet.summary, packet.value].filter(Boolean).join(' · '), packet.observationCount];
            for (const value of values)
            {
                const cell = document.createElement('td');
                cell.textContent = String(value ?? '');
                cell.title = cell.textContent;
                row.append(cell);
            }
            fragment.append(row);
        }
        fragment.append(spacer((view.total - view.first - view.rows.length) * height));
        body.replaceChildren(fragment);
        viewport.dataset.scrollable = String(viewport.clientHeight < viewport.scrollHeight);
        anchor = view.rows[0]?.id ?? null;
        if (follow || requestedAnchor !== null)
        {
            const target = follow ? viewport.scrollHeight : view.first * height + requestedOffset;
            if (viewport.scrollTop !== target)
            {
                viewport.scrollTop = target;
                programmedScroll = viewport.scrollTop;
            }
        }
        report(view, follow);
    }

    viewport.addEventListener('scroll', () =>
    {
        if (viewport.scrollTop === programmedScroll)
        {
            programmedScroll = -1;
            return;
        }
        programmedScroll = -1;
        anchor = null;
        follow = viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop < height;
        refresh();
    }, { signal: ctx.signal, passive: true });
    viewport.addEventListener('wheel', event =>
    {
        if (event.deltaY < 0)
        {
            follow = false;
            programmedScroll = -1;
            anchor = null;
        }
    }, { signal: ctx.signal, passive: true });
    body.addEventListener('click', event =>
    {
        const row = event.target.closest('[data-packet]');
        if (row)
        {
            selected = Number(row.dataset.packet);
            panels.dataset.empty = 'false';
            anchor = null;
            follow = false;
            send({ type: 'detail', id: selected });
            refresh();
        }
    }, { signal: ctx.signal });
    const resize = new ResizeObserver(refresh);
    resize.observe(viewport);
    return { refresh, render,

        /** 選択を変えたときだけ詳細を差し替えます。 */
        detail(value)
        {
            if (value.id !== selected)
            {
                return;
            }
            description.textContent = value.pending ? ctx.t('Loading packet details…', 'パケットの詳細を取得しています…')
                : value.removed ? ctx.t('Packet details have expired or the source is disconnected.', '詳細の保持期限が切れたか、取得側との接続が終了しています。')
                    : value.time + '\n' + value.observers.join('\n') + '\n' + value.rawLength + ' / ' + value.originalLength + ' bytes\n\n' + (value.details ?? '');
            hex.textContent = value.hex ?? '';
        },

        /** 表示方式を切り替えます。 */
        names(value)
        {
            hostNames = value;
            refresh();
        },

        get following()
        {
            return follow;
        },

        /** 一覧の追従状態を切り替えます。 */
        latest(enabled = true)
        {
            follow = enabled;
            anchor = null;
            selected = null;
            refresh();
        },

        /** 通信を維持して表示履歴だけを消します。 */
        clear()
        {
            selected = null;
            anchor = null;
            follow = true;
            description.textContent = hex.textContent = '';
            refresh();
        },

        /** レイアウト監視の所有権を解放します。 */
        dispose()
        {
            resize.disconnect();
        } };
}
