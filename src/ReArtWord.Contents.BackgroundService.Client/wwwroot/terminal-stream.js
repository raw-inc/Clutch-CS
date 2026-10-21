// One persistent connection owns input ordering and output backpressure. Replaying
// an old terminal query must not generate a new response at the shell prompt.
/** 入力の順序・確認応答・出力処理の寿命を管理します。
 * @returns ready・close・input・resizeを含む結果です。
 */
export function createTerminalStream(term, peer, session, options)
{
    const expectedHttps = 'https:';

    const url = new URL('/client/terminal', location.href);
    url.protocol = url.protocol === expectedHttps ? 'wss:' : 'ws:';
    url.search = new URLSearchParams({ cluster: peer.clusterId, peer: peer.peerId, instance: peer.instanceId, session: session.sessionId, owner: session.owner });
    const socket = new WebSocket(url), encoder = new TextEncoder(), buffer = new Uint8Array(8192);
    let closed = false, ready = false, replaying = !options.created, writing = false, sending = false, offset = 0, sequence = session.sequence;
    let input = '', inputBytes = 0, resizePending = true, sentColumns = 0, sentRows = 0, timer, ackTimer;

    /** 現在の処理が有効な実行世代かを確認します。
     * @returns 呼出し元が使用する判定結果です。
     */
    const active = () => !closed && options.active();

    const opening = setTimeout(() => fail('connect'), 15000);
    term.options.disableStdin = replaying;

    /** 固定された通信形式で入力を送信します。
     * @returns 呼出し元が使用する値です。
     */
    function send(value)
    {
        if (!active() || socket.readyState !== WebSocket.OPEN)
        {
            return false;
        }
        if ((65536) < (socket.bufferedAmount))
        {
            fail('backpressure');
            return false;
        }
        socket.send(JSON.stringify(value));
        return true;
    }

    /** 保持している入力または描画要求を送出します。 */
    function flush()
    {
        timer = null;
        if (!active() || !ready || sending || (!input && !resizePending))
        {
            return;
        }
        const encoded = encoder.encodeInto(input, buffer);
        const columns = Math.max(20, Math.min(400, term.cols)), rows = Math.max(5, Math.min(200, term.rows));
        const data = btoa(String.fromCharCode(...buffer.subarray(0, encoded.written)));
        if (!send({ type: 'input', request: { sessionId: session.sessionId, owner: session.owner, sequence, data, columns, rows } }))
        {
            return;
        }
        input = input.slice(encoded.read);
        inputBytes -= encoded.written;
        resizePending = false;
        sentColumns = columns;
        sentRows = rows;
        sending = true;
        ackTimer = setTimeout(() => fail('input-unconfirmed'), 10000);
    }

    /** 重複した描画要求を次の一回にまとめます。 */
    function schedule()
    {
        if ((input || resizePending) && !timer && !sending && ready)
        {
            timer = setTimeout(flush, 8);
        }
    }

    /** 送信または処理する項目を待機列へ追加します。 */
    function enqueue(data)
    {
        if (!active() || replaying)
        {
            return;
        }
        const size = encoder.encode(data).length;
        if ((1024 * 1024) < (inputBytes + size))
        {
            options.problem('input-full');
            return;
        }
        input += data;
        inputBytes += size;
        schedule();
    }

    const dataSubscription = term.onData(data => enqueue(options.transformInput?.(data) ?? data));

    /** 対象を閉じ、関連する状態を整理します。 */
    function close()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        ready = false;
        clearTimeout(opening);
        clearTimeout(timer);
        clearTimeout(ackTimer);
        dataSubscription?.dispose();
        input = '';
        inputBytes = 0;
        term.options.disableStdin = true;
        socket.onmessage = socket.onerror = socket.onclose = null;
        socket.close();
    }

    /** 失敗を通知し、後続の処理を停止します。 */
    function fail(reason)
    {
        if (!active())
        {
            return;
        }
        close();
        options.closed(reason);
    }

    socket.onerror = () => fail('connection');
    socket.onclose = () => fail('connection');
    socket.onmessage = event =>
    {
        if (!active())
        {
            return;
        }
        try
        {
            const expectedOutput = 'output';
            const expectedReady = 'ready';
            const expectedAck = 'ack';
            const expectedClosed = 'closed';

            const message = JSON.parse(event.data);
            switch (message.type)
            {
                case expectedOutput:
                    {
                        if (writing || message.output.session.owner !== session.owner)
                        {
                            fail('protocol');
                            return;
                        }
                        writing = true;
                        const output = message.output;
                        if (output.offset !== offset)
                        {
                            term.reset();
                            term.write('\r\n[Earlier output expired]\r\n');
                        }
                        term.write(Uint8Array.from(atob(output.data), char => char.charCodeAt(0)), () =>
                        {
                            if (!active())
                            {
                                return;
                            }
                            writing = false;
                            offset = output.nextOffset;
                            send({ type: 'consumed', offset });
                        });
                    }
                    break;
                case expectedReady:
                    {
                        if (writing)
                        {
                            fail('protocol');
                            return;
                        }
                        clearTimeout(opening);
                        ready = !message.session.exited;
                        replaying = false;
                        term.options.disableStdin = !ready;
                        options.ready();
                        schedule();
                    }
                    break;
                case expectedAck:
                    {
                        if (!sending || message.session.owner !== session.owner || message.session.sequence !== sequence + 1)
                        {
                            fail('protocol');
                            return;
                        }
                        clearTimeout(ackTimer);
                        sequence = message.session.sequence;
                        sending = false;
                        schedule();
                    }
                    break;
                case expectedClosed:
                    {
                        close();
                        options.closed(message.reason, message.session);
                    }
                    break;
                default:
                    {
                        fail('protocol');
                    }
                    break;
            }
        }
        catch
        {
            fail('protocol');
        }
    };
    return {
        get ready()
        {
            return ready;
        },

        close,

        /** 型と初期値を持つ入力欄を作成します。 */
        input(data)
        {
            if (ready)
            {
                enqueue(data);
            }
        },

        /** 現在の表示寸法を処理先へ通知します。 */
        resize()
        {
            if (term.cols !== sentColumns || term.rows !== sentRows)
            {
                resizePending = true;
                schedule();
            }
        }
    };
}
