const maximumTextBytes = 65536;

/** テキスト形式の存在を確認し、画像だけのクリップボードを空文字へ変換しません。
 * @returns テキスト、または転送対象がなければnullです。
 */
export async function readDesktopClipboardTextAsync(clipboard)
{
    if (clipboard?.read)
    {
        const items = await clipboard.read();
        const item = items.find(candidate => candidate.types.includes('text/plain'));
        if (!item)
        {
            return null;
        }
        const blob = await item.getType('text/plain');
        if ((maximumTextBytes) < (blob.size))
        {
            throw new Error('Clipboard.InvalidOrLargeText');
        }
        return new TextDecoder('utf-8', { fatal: true }).decode(await blob.arrayBuffer());
    }
    if (clipboard?.readText)
    {
        const text = await clipboard.readText();

        // 旧APIの空文字は非テキストとも区別できないため、明示pasteイベントだけで空文字を受理します。
        return text.length ? text : null;
    }
    throw new Error('Clipboard.Permission');
}

/** 文字列を切り詰めず、長さ付きのテキスト要求へ変換します。
 * @returns 一回の入力メッセージです。
 */
export function desktopClipboardRequest(id, value)
{
    if (!Number.isInteger(id) || id < 1 || (2147483647) < (id) || typeof value !== 'string' || (maximumTextBytes) < (value.length) || value.includes('\0'))
    {
        throw new Error('Clipboard.InvalidOrLargeText');
    }
    const text = new TextEncoder().encode(value);
    if ((maximumTextBytes) < (text.length) || new TextDecoder().decode(text) !== value)
    {
        throw new Error('Clipboard.InvalidOrLargeText');
    }
    const message = new ArrayBuffer(24 + text.length), header = new DataView(message);
    header.setInt32(0, 16);
    header.setInt32(4, id);
    header.setInt32(8, text.length);
    new Uint8Array(message, 24).set(text);
    return message;
}

/** ホストの更新と反映確認を、有限のテキストだけとして解釈します。
 * @returns 通知の種類、要求ID、更新番号、テキストです。
 */
export function readDesktopClipboard(payload)
{
    if (!(payload instanceof Uint8Array) || payload.length < 12 || (maximumTextBytes + 12) < (payload.length))
    {
        throw new Error('Invalid desktop clipboard frame');
    }
    const header = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const mode = header.getInt32(0), id = header.getInt32(4), version = header.getUint32(8);
    const text = new TextDecoder('utf-8', { fatal: true }).decode(payload.subarray(12));
    if (mode < 0 || (2) < (mode) || id < 0 || mode === 0 && id !== 0 || mode === 1 && (id === 0 || text.length) || text.includes('\0'))
    {
        throw new Error('Invalid desktop clipboard notification');
    }
    return { mode, id, version, text };
}

/** クリップボードの権限待ち、更新順序、貼り付け前の確認を一接続の寿命へ閉じます。
 * @returns 同期の選択、接続、入力と通知を扱う操作です。
 */
export function createDesktopClipboard(options)
{
    let enabled = false, available = false, active = false, entered = false, epoch = 0, nextId = 0;
    let pending = null, local = null, gate = false, flushing = false, version = null, knownLocal = null;
    let hostWrites = Promise.resolve(), hostWriting = false, drainingHost = false, latestHost = null, blockedHost = null, pasteTimer = null;
    const queued = [];

    /** 待機中の要求を元の一回だけ完了させます。 */
    function settle(error = null)
    {
        const current = pending;
        pending = null;
        if (current)
        {
            clearTimeout(current.timer);
            if (error)
            {
                current.reject(error);
            }
            else
            {
                current.resolve();
            }
        }
    }

    /** 切断・非アクティブ化後の非同期結果と待機入力を破棄します。 */
    function cancel()
    {
        const release = gate;
        epoch++;
        local = null;
        gate = false;
        latestHost = null;
        blockedHost = null;
        clearTimeout(pasteTimer);
        pasteTimer = null;
        queued.length = 0;
        settle(new Error('Clipboard.Cancelled'));
        if (release)
        {
            options.release();
        }
    }

    /** ブラウザーからpasteイベントを受けたら、補助読取りの予約を取り消します。 */
    function pasted()
    {
        clearTimeout(pasteTimer);
        pasteTimer = null;
    }

    /** 読み取った時点と同じ操作領域・接続だけを許可します。
     * @returns 現在の操作へ反映できるかです。
     */
    function current(stamp)
    {
        return stamp === epoch && enabled && available && active && options.focused?.() !== false;
    }

    /** テキスト反映の確認前に、後続のキー入力を流しません。
     * @returns ホストが同じ要求の反映を確認したPromiseです。
     */
    function transferAsync(value, stamp)
    {
        if (!current(stamp))
        {
            return Promise.reject(new Error('Clipboard.Cancelled'));
        }
        nextId = nextId % 2147483647 + 1;
        const message = desktopClipboardRequest(nextId, value);
        gate = true;
        return new Promise((resolve, reject) =>
        {
            pending = { id: nextId, stamp, resolve, reject, timer: setTimeout(() => settle(new Error('Clipboard.Timeout')), 4000) };
            try
            {
                if (!options.packet(message))
                {
                    settle(new Error('Clipboard.Disconnected'));
                }
            }
            catch
            {
                settle(new Error('Clipboard.Disconnected'));
            }
        });
    }

    /** 同じ接続へだけ、貼り付け以降に受けた入力を順序どおり送ります。 */
    function flush()
    {
        gate = false;
        flushing = true;
        try
        {
            for (const input of queued.splice(0))
            {
                options.input(...input);
            }
        }
        finally
        {
            flushing = false;
        }
    }

    /** 新しいクライアントテキストだけを同期し、非テキストや未変更時はホストの内容を保持します。
     * @returns 一回の同期の完了です。
     */
    async function synchronizeAsync(value, paste)
    {
        if (!enabled || !available || !active || local)
        {
            return;
        }
        const stamp = ++epoch, restoreLocal = hostWriting, previousLocal = knownLocal;
        blockedHost = null;
        local = stamp;
        gate = paste;
        try
        {
            // 権限取得に必要なユーザー操作を失わないよう、最初のawaitより前に読取りを開始します。
            const reading = value === undefined ? options.read() : Promise.resolve(value);
            const text = await reading;
            if (!current(stamp))
            {
                return;
            }
            if (text === null)
            {
                // ホスト上の画像・ファイルをそのまま貼り付け、次に現れたテキストは新しいコピーとして扱います。
                knownLocal = null;
            }
            else
            {
                await hostWrites;
                if (!current(stamp))
                {
                    return;
                }
                if (text !== previousLocal)
                {
                    if (restoreLocal)
                    {
                        await options.write(text);
                    }
                    await transferAsync(text, stamp);
                    if (!current(stamp))
                    {
                        return;
                    }
                    knownLocal = text;
                }
            }
            if (paste)
            {
                flushing = true;
                try
                {
                    options.paste();
                }
                finally
                {
                    flushing = false;
                }
            }
            flush();
        }
        catch (error)
        {
            if (current(stamp))
            {
                queued.length = 0;
                gate = false;
                options.release();
                options.error(error.message?.startsWith('Clipboard.') ? error.message : 'Clipboard.Permission');
            }
        }
        finally
        {
            if (local === stamp)
            {
                local = null;
                gate = false;
            }
        }
    }

    /** 操作領域へ戻った時点で、新しくコピーされたクライアントテキストを優先します。
     * @returns 権限とホスト反映の確認です。
     */
    async function activateAsync()
    {
        if (entered || !enabled || !available || options.focused?.() === false)
        {
            return;
        }
        active = true;
        entered = true;
        await synchronizeAsync(undefined, false);
    }

    /** 同期の選択を更新し、利用可能な接続の購読と初回同期へ反映します。
     * @returns 有効化直後のクライアント優先同期です。
     */
    async function enableAsync(value)
    {
        cancel();
        enabled = value;
        active = false;
        entered = false;
        if (available)
        {
            options.control(value);
        }
        if (value)
        {
            await activateAsync();
        }
    }

    /** 旧版・切断・閲覧専用では入力と購読を停止します。 */
    function connect(value)
    {
        if (available === value)
        {
            return;
        }
        cancel();
        if (available && enabled)
        {
            options.control(false);
        }
        available = value;
        active = false;
        entered = false;
        version = null;
        if (available && enabled)
        {
            options.control(true);
        }
    }

    /** 別の画面でコピーした内容を、遅れて届いたホスト通知で上書きしません。 */
    function leave()
    {
        active = false;
        entered = false;
        cancel();
    }

    /** 同じ文字列でも、画面外で明示的にコピーし直した内容を新しいものとして扱います。 */
    function copied()
    {
        knownLocal = null;
        leave();
    }

    /** コピー直後にポインターを外へ動かしても、アクティブな画面へのホスト更新は受け取ります。 */
    function leaveArea()
    {
        entered = false;
    }

    /** 貼り付けイベントの本文が新しければ同期し、それ以外はホストの内容を貼り付けます。
     * @returns ホスト反映と一回の貼り付けの完了です。
     */
    async function pasteAsync(value)
    {
        if (!enabled || !available || options.focused?.() === false)
        {
            return;
        }
        cancel();
        active = true;
        entered = true;
        await synchronizeAsync(value, true);
    }

    /** 更新番号と要求IDを検査し、古い反映確認や自己更新の循環を除きます。 */
    function receive(payload)
    {
        const frame = readDesktopClipboard(payload);
        if (frame.mode === 1 || frame.mode === 2 && frame.id !== 0)
        {
            if (pending?.id === frame.id && current(pending.stamp))
            {
                if (frame.mode === 1)
                {
                    version = frame.version;
                }
                settle(frame.mode === 2 ? new Error(frame.text) : null);
            }
            return;
        }
        if (!current(epoch) || local)
        {
            return;
        }
        if (frame.mode === 2)
        {
            options.error('Clipboard.InvalidOrLargeText');
            return;
        }
        const distance = version === null ? 1 : (frame.version - version) >>> 0;
        if (distance === 0 || (2147483648) <= (distance))
        {
            return;
        }
        version = frame.version;
        if (blockedHost)
        {
            blockedHost = { text: frame.text, stamp: epoch };
            return;
        }
        latestHost = { text: frame.text, stamp: epoch };
        if (!drainingHost)
        {
            hostWrites = writeHostAsync();
        }
    }

    /** 遅い権限待ちの間も、最新のホスト更新一件だけを保持します。
     * @returns 現在のホスト更新の反映完了です。
     */
    async function writeHostAsync()
    {
        drainingHost = true;
        try
        {
            while (latestHost)
            {
                const item = latestHost;
                latestHost = null;
                if (!current(item.stamp) || local || item.text === knownLocal)
                {
                    continue;
                }
                hostWriting = true;
                try
                {
                    await options.write(item.text);

                    // 待機中に操作が変わっても、完了した書込みの内容は記憶し、古い内容の逆送を防ぎます。
                    knownLocal = item.text;
                }
                catch
                {
                    if (current(item.stamp))
                    {
                        blockedHost = latestHost ?? item;
                        latestHost = null;
                        options.error('Clipboard.WritePermission');
                    }
                }
                finally
                {
                    hostWriting = false;
                }
            }
        }
        finally
        {
            drainingHost = false;
        }
    }

    /** 書込み許可にユーザー操作が必要な場合、同じ操作領域で保持した最新テキストだけを再試行します。 */
    function retryHost()
    {
        if (blockedHost && current(blockedHost.stamp) && !local && !drainingHost)
        {
            latestHost = blockedHost;
            blockedHost = null;
            hostWrites = writeHostAsync();
        }
    }

    /** ブラウザー標準のpasteイベントを妨げず、ホストへ先にCtrl+Vを送らせません。
     * @returns このキーを同期経路で扱うかです。
     */
    function key(event, down = true)
    {
        const expectedV = 'KeyV', expectedInsert = 'Insert';
        const paste = !event.altKey && !event.metaKey && (event.ctrlKey && event.code === expectedV || event.shiftKey && event.code === expectedInsert);
        if (!enabled || !available || !paste)
        {
            return false;
        }
        if (event.repeat)
        {
            event.preventDefault();
        }
        else if (down)
        {
            clearTimeout(pasteTimer);

            // pasteイベントを提供しないブラウザーでは、同じユーザー操作の読取りへ切り替えます。
            pasteTimer = setTimeout(() => pasteAsync(), 100);
        }
        return true;
    }

    /** テキスト反映中の入力だけを有限に保持し、切断や解放では破棄します。
     * @returns 今回の入力を保留または破棄したかです。
     */
    function hold(kind, ...arguments_)
    {
        if (kind === 5)
        {
            cancel();
            return false;
        }
        if (!gate || flushing || ![1, 2, 3, 4, 10].includes(kind))
        {
            return false;
        }
        if (kind === 1)
        {
            return true;
        }
        if ((64) <= (queued.length))
        {
            cancel();
            options.error('Clipboard.InputOverflow');
            return true;
        }
        queued.push([kind, ...arguments_]);
        return true;
    }

    return { enableAsync, activateAsync, pasteAsync, connect, leave, copied, leaveArea, receive, key, hold, pasted, retryHost,

        get enabled()
        {
            return enabled;
        },

        get available()
        {
            return available;
        } };
}
