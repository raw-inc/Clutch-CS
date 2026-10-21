import { parseScopeSearch } from './signal-scope-search.js';

/** 取得側の履歴検索と、一件ずつの詳細取得の寿命を所有します。
 * @returns 制御応答、選択、破棄の操作です。
 */
export function scopeRemote(history, connections, tell, send, changed)
{
    let revision = 0, selection = 0, queryTimer = null, selected = null;
    let query = { requestId: 0, expression: { term: '' }, mode: 'text' };

    /** 検索中であることを、通常検索とは独立して表示します。 */
    function progress(error = null)
    {
        tell('advanced', { pending: queryTimer !== null || [...connections.values()].some(connection => connection.searching), error });
    }

    /** 同じhostには一度に一検索だけを送り、途中の入力を最新へまとめます。 */
    function search(connection)
    {
        if (connection.searching || !connection.catalog || connection.socket.readyState !== WebSocket.OPEN || !query.requestId || connection.searched === query.requestId)
        {
            return;
        }
        const request = { id: query.requestId, ids: new Set(), timer: null };
        connection.searching = request;
        send(connection, { type: 'search', ...query });
        request.timer = setTimeout(() =>
        {
            if (connection.searching === request)
            {
                connection.searching = null;
                connection.searched = request.id;
                progress('Advanced search timed out on ' + connection.host + '.');
                search(connection);
            }
        }, 15000);
        progress();
    }

    /** 取得側がまとめて返すIDだけを受け、本文を一覧へ取り込みません。 */
    function results(connection, message)
    {
        const request = connection.searching;
        if (!request || request.id !== message.requestId)
        {
            return;
        }
        if (message.reset)
        {
            request.ids.clear();
        }
        for (const id of message.ids ?? [])
        {
            if ((250000) <= (request.ids.size))
            {
                throw new Error('Advanced search result exceeds the source history limit.');
            }
            request.ids.add(id);
        }
        if (message.complete)
        {
            clearTimeout(request.timer);
            connection.searching = null;
            connection.searched = request.id;
            if (request.id === query.requestId)
            {
                connection.matches = { requestId: request.id, ids: request.ids };
            }
            history.applyAdvanced(connection.key, request.id, request.ids);
            changed();
            search(connection);
            progress();
        }
    }

    /** 選択を変えた場合、古い詳細応答を表示させません。 */
    function cancelSelection()
    {
        clearTimeout(selected?.timer);
        selected = null;
    }

    /** 同じパケットを保持する別の観測元があれば、期限切れ時に参照します。 */
    function requestDetail()
    {
        const pending = selected;
        if (!pending)
        {
            return;
        }
        clearTimeout(pending.timer);
        while (pending.references.length && performance.now() < pending.deadline)
        {
            const reference = pending.references.shift();
            const connection = [...connections.values()].find(item => item.key === reference.connection && item.socket.readyState === WebSocket.OPEN);
            if (connection)
            {
                pending.requestId = ++selection;
                pending.connection = connection.key;
                send(connection, { type: 'detail', packetId: reference.packetId, requestId: pending.requestId });
                pending.timer = setTimeout(requestDetail, 3000);
                return;
            }
        }
        tell('detail', { id: pending.id, removed: true });
        selected = null;
    }

    /** 選択した原本だけをHexへ変換します。
     * @returns 有界な表示文字列です。
     */
    function hex(raw)
    {
        const lines = [];
        for (let offset = 0; offset < raw.length; offset += 16)
        {
            const bytes = raw.subarray(offset, offset + 16);
            const values = [...bytes].map(value => value.toString(16).padStart(2, '0').toUpperCase()).join(' ').padEnd(47);
            const ascii = [...bytes].map(value => (32) <= (value) && value <= 126 ? String.fromCharCode(value) : '.').join('');
            lines.push(offset.toString(16).padStart(4, '0').toUpperCase() + '  ' + values + '  ' + ascii);
        }
        return lines.join('\n');
    }

    /** 取得側の応答には選択時の識別子を必須とします。 */
    function detail(connection, value)
    {
        if (!selected || value.requestId !== selected.requestId || connection.key !== selected.connection)
        {
            return;
        }
        if (value.removed)
        {
            requestDetail();
            return;
        }
        tell('detail', { id: selected.id, details: value.details, rawLength: value.raw.length, originalLength: value.originalLength,
            time: value.time, observers: selected.observers, observationCount: selected.observationCount, hex: hex(value.raw) });
        cancelSelection();
    }

    return { detail,

        /** 検索応答より遅れて届いた一覧にも、同じ検索版の一致を反映します。 */
        packet(connection, packet)
        {
            if (connection.matches?.requestId === query.requestId && packet.queryId !== query.requestId)
            {
                packet.queryId = query.requestId;
                packet.matched = connection.matches.ids.has(packet.packetId);
            }
        },

        /** 原本の保持上限とhost名だけを伝えます。 */
        configure(connection, names)
        {
            if (!connection.catalog || connection.socket.readyState !== WebSocket.OPEN)
            {
                return;
            }
            const count = Math.max(1, [...connections.values()].filter(item => item.catalog).length);
            send(connection, { type: 'configure', maximumCount: Math.max(1, Math.floor(history.limit / count)), maximumBytes: Math.max(65536, Math.floor(history.maximumBytes / count)), hostName: connection.host,
                names: [...names].slice(0, 1024).map(([address, name]) => ({ address, name })) });
            search(connection);
        },

        /** Advancedの有効な入力だけを取得側へ送り、不完全な式は前回を維持します。 */
        advanced(text, mode)
        {
            const hexMode = 'hex';
            let expression;
            if (mode === hexMode)
            {
                const compact = text.replace(/\s/gu, '');
                if ((2048) < (compact.length) || compact.length % 2 || !/^[0-9a-f]*$/iu.test(compact))
                {
                    throw new Error('Hex search requires complete byte pairs, such as 2F 7A 6F 6E 65.');
                }
                expression = { term: compact };
            }
            else
            {
                expression = parseScopeSearch(text);
            }
            query = { requestId: ++revision, expression, mode };
            history.setAdvanced(text.trim() ? query.requestId : 0);
            changed();
            clearTimeout(queryTimer);
            queryTimer = setTimeout(() =>
            {
                queryTimer = null;
                for (const connection of connections.values())
                {
                    search(connection);
                }
                progress();
            }, 150);
            progress();
        },

        /** 一覧行の参照番号だけを使い、選択した原本を要求します。 */
        select(id)
        {
            cancelSelection();
            const row = history.byId.get(id);
            if (!row)
            {
                tell('detail', { id, removed: true });
                return;
            }
            selected = { id, references: [...row.references], deadline: performance.now() + 10000, observers: row.observers, observationCount: row.observationCount };
            tell('detail', { id, pending: true });
            requestDetail();
        },

        /** 本文を伴わない制御応答を受けます。 */
        control(connection, message)
        {
            const searchType = 'search', detailType = 'detail', clearType = 'cleared';
            if (message.type === searchType)
            {
                results(connection, message);
            }
            else if (message.type === detailType)
            {
                detail(connection, message);
            }
            else if (message.type === clearType)
            {
                connection.matches = null;
                clearTimeout(connection.searching?.timer);
                connection.searching = null;
                connection.searched = 0;
                search(connection);
                progress();
            }
        },

        /** 不正条件への応答で検索待ちを残しません。 */
        failed(connection, message)
        {
            if (connection.searching && connection.searching.id === message.requestId)
            {
                clearTimeout(connection.searching.timer);
                connection.searching = null;
                connection.searched = message.requestId;
                progress(message.message);
            }
        },

        /** 接続を失った取得側への保留処理を回収します。 */
        disconnected(connection)
        {
            clearTimeout(connection.searching?.timer);
            connection.searching = null;
            progress();
            if (selected?.connection === connection.key)
            {
                requestDetail();
            }
        },

        /** クリアは取得側にも伝え、原本を延命しません。 */
        clear()
        {
            cancelSelection();
            for (const connection of connections.values())
            {
                connection.matches = null;
                if (connection.socket.readyState === WebSocket.OPEN)
                {
                    send(connection, { type: 'clear' });
                }
            }
        },

        /** 詳細と検索の待機を画面の寿命内に収めます。 */
        dispose()
        {
            cancelSelection();
            clearTimeout(queryTimer);
            for (const connection of connections.values())
            {
                clearTimeout(connection.searching?.timer);
            }
        } };
}
