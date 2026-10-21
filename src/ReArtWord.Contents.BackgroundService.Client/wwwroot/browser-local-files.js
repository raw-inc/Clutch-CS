/** ファイル名として扱えない値を拒否します。
 * @returns 呼出し元が使用する判定結果です。
 */
const validName = name => typeof name === 'string' && !!name && !/[\\/:]/.test(name) && !['.', '..'].includes(name);

/** ローカル領域の操作に必要な権限を確認します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function requireLocalPermission(handle, mode = 'read')
{
    const expectedGranted = 'granted';

    if (!handle || await handle.queryPermission({ mode }) !== expectedGranted)
    {
        throw new Error('Folder access is not granted. Choose the local folder again.');
    }
}

/** 指定した相対パスのローカルディレクトリを解決します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function localDirectory(root, path = '')
{
    await requireLocalPermission(root);
    let directory = root;
    for (const name of path ? path.split('/') : [])
    {
        if (!validName(name))
        {
            throw new Error('The path must stay inside the selected local folder.');
        }
        directory = await directory.getDirectoryHandle(name);
    }
    return directory;
}

/** ローカルディレクトリの項目を一覧にします。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function listLocalFiles(root, path, signal)
{
    const directory = await localDirectory(root, path), entries = [];
    for await (const handle of directory.values())
    {
        signal.throwIfAborted();
        if ((4096) <= (entries.length))
        {
            throw new Error('This folder exceeds 4096 entries. Choose a smaller folder.');
        }
        if (!validName(handle.name))
        {
            throw new Error('Invalid local entry name.');
        }
        entries.push({ name: handle.name, path: path ? path + '/' + handle.name : handle.name, isDirectory: handle.kind === 'directory', handle });
    }
    return { directory, entries };
}

/** 同名のファイル・フォルダーが存在する場合は作成を拒否します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
export async function requireLocalAbsenceAsync(parent, name)
{
    if (!validName(name))
    {
        throw new Error('Invalid destination name.');
    }
    for (const method of ['getFileHandle', 'getDirectoryHandle'])
    {
        try
        {
            await parent[method](name);
        }
        catch (error)
        {
            const expectedNotfounderror = 'NotFoundError';

            if (error.name === expectedNotfounderror)
            {
                continue;
            }
            throw error;
        }
        throw new Error('The destination already exists: ' + name);
    }
}

/** ブラウザーから取得するファイルのURLを構築します。
 * @returns 呼出し元が使用する値です。
 */
export function browserDownloadUrl(target, path)
{
    return '/client/browser-files/download?' + new URLSearchParams({ clusterId: target.clusterId, peerId: target.peerId, instanceId: target.instanceId, path });
}

// Each file is streamed into a browser-owned temporary write and closed only
// after the response ends. Folder copies can be partially complete on failure.
/** ファイルをストリームで取得し、検証した内容をローカルへ確定します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function downloadToLocal(entry, destination, target, api, signal, { progress = () =>
{}, fetchFile = globalThis.fetch } = {})
{
    await requireLocalPermission(destination, 'readwrite');
    const plan = [];

    /** 配下の項目を収集し、処理する一覧を組み立てます。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function collectAsync(item, names, depth)
    {
        signal.throwIfAborted();
        if (!validName(item.name) || (32) < (depth) || (4096) <= (plan.length))
        {
            throw new Error('Folder exceeds the supported name, depth or entry limits.');
        }
        plan.push({ entry: item, names });
        if (!item.isDirectory)
        {
            return;
        }
        let offset = 0, more = true;
        do
        {
            const page = await api('/api/files/browse', target, { path: item.path, offset, fromDrives: true }, signal);
            if (page.problem || page.path !== item.path)
            {
                throw new Error(page.problem || 'Source folder changed.');
            }
            for (const child of page.entries)
            {
                const parent = item.path.replace(/\\/g, '/').replace(/\/$/, '') + '/';
                if (child.path.replace(/\\/g, '/') !== parent + child.name)
                {
                    throw new Error('Source entry is outside its folder.');
                }
                await collectAsync(child, [...names, child.name], depth + 1);
            }
            if (page.nextOffset == null)
            {
                more = false;
            }
            else
            {
                if (!Number.isSafeInteger(page.nextOffset) || page.nextOffset <= offset)
                {
                    throw new Error('Invalid folder continuation.');
                }
                offset = page.nextOffset;
            }
        } while (more);
    }

    await collectAsync(entry, [entry.name], 0);
    await requireLocalAbsenceAsync(destination, entry.name);
    const folders = new Map([['', destination]]);
    let copied = 0;
    for (const item of plan)
    {
        signal.throwIfAborted();
        const name = item.names.at(-1), parent = folders.get(item.names.slice(0, -1).join('/'));
        await requireLocalAbsenceAsync(parent, name);
        if (item.entry.isDirectory)
        {
            folders.set(item.names.join('/'), await parent.getDirectoryHandle(name, { create: true }));
            continue;
        }
        const response = await fetchFile(browserDownloadUrl(target, item.entry.path), { signal, cache: 'no-store' });
        const length = await readDownloadLengthAsync(response);
        let writer, reader, closing = false, received = 0;
        try
        {
            await requireLocalAbsenceAsync(parent, name);
            const file = await parent.getFileHandle(name, { create: true });
            if ((await file.getFile()).size !== 0)
            {
                throw new Error('Destination changed before writing.');
            }
            writer = await file.createWritable({ keepExistingData: false, mode: 'exclusive' });
            reader = response.body.getReader();
            let reading = true;
            while (reading)
            {
                signal.throwIfAborted();
                const { done, value } = await reader.read();
                if (done)
                {
                    reading = false;
                    continue;
                }
                received += value.byteLength;
                if ((length) < (received))
                {
                    throw new Error('Download length changed.');
                }
                await writer.write(value);
                progress(copied + received);
            }
            if (received !== length)
            {
                throw new Error('Download is incomplete.');
            }
            signal.throwIfAborted();
            closing = true;
            await writer.close();
            copied += received;
        }
        catch (error)
        {
            if (closing)
            {
                throw new Error('Local save result is unconfirmed. Refresh the destination before retrying.', { cause: error });
            }
            try
            {
                await writer?.abort();
            }
            catch
            { /* 他のアプリが変更した可能性があるため、パスを削除せず元の失敗を維持します。 */ }
            throw error;
        }
        finally
        {
            if (reader)
            {
                try
                {
                    await reader.cancel();
                }
                catch
                { /* 読取りの終了後のため、取消しの失敗で元の受信・保存結果を置き換えません。 */ }
                reader.releaseLock();
            }
            else
            {
                try
                {
                    await response.body.cancel();
                }
                catch
                { /* 読取りの終了後のため、取消しの失敗で元の受信・保存結果を置き換えません。 */ }
            }
        }
    }
    return copied;
}

/** 書込み開始前に応答と長さを検証し、不正な応答のストリームを解放します。
 * @returns 検証したファイル長です。
 */
async function readDownloadLengthAsync(response)
{
    if (!response.ok || !response.body)
    {
        await response.body?.cancel();
        throw new Error('Download unconfirmed (' + response.status + ').');
    }
    const length = Number(response.headers.get('content-length'));
    if (!Number.isSafeInteger(length) || length < 0 || response.headers.get('content-length') == null)
    {
        await response.body.cancel();
        throw new Error('Download size is unconfirmed.');
    }
    return length;
}
