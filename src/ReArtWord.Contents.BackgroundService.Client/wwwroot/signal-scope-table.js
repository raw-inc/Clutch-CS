/** 固定行高の可視範囲だけを描画し、選択詳細は新着で消しません。
 * @returns Workerの一覧と詳細を表示する操作です。
 */
export function scopeTable(container, ctx, send, report)
{
    const height = 26, viewport = document.createElement('div'), table = document.createElement('table');
    const header = document.createElement('thead'), labels = document.createElement('tr'), body = document.createElement('tbody');
    viewport.className = 'scope-list';
    viewport.tabIndex = 0;
    viewport.setAttribute('aria-label', ctx.t('Captured packets', '取得したパケット'));
    table.className = 'scope-packets';
    for (const label of ['#', ctx.t('Time', '時刻'), 'Protocol', 'Source', 'Destination', 'Info', ctx.t('Seen', '観測')])
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
    for (const [name, content] of [[ctx.t('Packet details', 'パケット詳細'), description], ['Hex / ASCII', hex]])
    {
        const section = document.createElement('section'), title = document.createElement('h2');
        title.textContent = name;
        section.append(title, content);
        panels.append(section);
    }
    container.append(viewport, panels);
    let follow = true, hostNames = false, selected = null, request = 0, programmedScroll = -1, anchor = null, anchorOffset = 0, requestedAnchor = null;
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
        requestedAnchor = anchor;
        send({ type: 'view', request: ++request, start: Math.max(0, Math.floor(viewport.scrollTop / height)), anchor, count: Math.min(200, Math.ceil(viewport.clientHeight / height) + 5), follow });
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
        anchorOffset = viewport.scrollTop % height;
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
        anchor = view.rows[0]?.id ?? null;
        if (follow || requestedAnchor !== null)
        {
            const target = follow ? viewport.scrollHeight : view.first * height + anchorOffset;
            if (viewport.scrollTop !== target)
            {
                viewport.scrollTop = target;
                programmedScroll = viewport.scrollTop;
            }
        }
        report(view);
    }

    viewport.addEventListener('scroll', () =>
    {
        if (viewport.scrollTop === programmedScroll)
        {
            return;
        }
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

        /** 最新行への追従を再開します。 */
        latest()
        {
            follow = true;
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
