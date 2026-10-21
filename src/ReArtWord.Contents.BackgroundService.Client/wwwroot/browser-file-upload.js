import { sha256 } from './vendor/noble-hashes/sha2.js';

/** バイト列を大文字の16進表記へ変換します。
 * @returns 呼出し元が使用する値です。
 */
const hex = data => Array.from(data, value => value.toString(16).padStart(2, '0')).join('').toUpperCase();

/** 取消しを確認しながらファイルを分割してSHA-256を計算します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function hashBrowserFile(file, signal, progress = () =>
{})
{
    const hash = sha256.create();
    try
    {
        for (let offset = 0; offset < file.size; offset += 1024 * 1024)
        {
            signal.throwIfAborted();
            hash.update(new Uint8Array(await file.slice(offset, offset + 1024 * 1024).arrayBuffer()));
            progress(Math.min(file.size, offset + 1024 * 1024));
        }
        signal.throwIfAborted();
        return hex(hash.digest());
    }
    finally
    {
        hash.destroy();
    }
}

// The manifest matches the Core's canonical, PascalCase single-file entry.
/** 単一ファイルのマニフェストを共通の表現で識別します。
 * @returns 呼出し元が使用する値です。
 */
export function fileManifest(length, hash)
{
    return hex(sha256(new TextEncoder().encode(JSON.stringify([{ RelativePath: '', IsDirectory: false, Length: length, Sha256: hash }]))));
}

/** ファイルを検証・分割送信し、最後に転送の確定を要求します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function uploadBrowserFile(file, target, directory, api, signal, progress = () =>
{})
{
    if (!file.name || /[\\/:]/.test(file.name) || ['.', '..'].includes(file.name) || !Number.isSafeInteger(file.size) || file.size < 0)
    {
        throw new Error('Invalid file name or size.');
    }
    const digest = await hashBrowserFile(file, signal, bytes => progress('hashing', bytes, file.size));
    return uploadEntriesAsync([{ relativePath: '', isDirectory: false, length: file.size, sha256: digest, file }], file.name, target, directory, fileManifest(file.size, digest), { api, signal, progress });
}

/** 選択したディレクトリを列挙し、相対パスを保ってアップロードします。
 * @returns 処理の完了と結果を伝えるPromiseです。
 * @contract 公開済みのJavaScriptメソッド名と呼出し側の契約を維持します。
 */
export async function uploadBrowserDirectory(handle, target, directory, api, signal, progress = () =>
{})
{
    const entries = [];

    /** 配下の要素を辿り、対象になる項目を収集します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function visitAsync(item, path, depth)
    {
        const expectedDirectory = 'directory';

        signal.throwIfAborted();
        if ((4096) <= (entries.length) || (32) < (depth) || (1024) < (path.length))
        {
            throw new Error('Folder exceeds 4096 entries or 32 levels. Select a smaller folder.');
        }
        if (item.kind === expectedDirectory)
        {
            entries.push({ relativePath: path, isDirectory: true, length: 0, sha256: '' });
            for await (const child of item.values())
            {
                if (!child.name || /[\\/:]/.test(child.name) || ['.', '..'].includes(child.name))
                {
                    throw new Error('Invalid folder entry name.');
                }
                await visitAsync(child, path ? path + '/' + child.name : child.name, depth + 1);
            }
        }
        else
        {
            const file = await item.getFile(), sha256 = await hashBrowserFile(file, signal, bytes => progress('hashing', bytes));
            entries.push({ relativePath: path, isDirectory: false, length: file.size, sha256, file });
        }
    }

    await visitAsync(handle, '', 0);
    const manifest = await api('/client/browser-files/manifest', null, entries.map(({ file, ...entry }) => entry), signal);
    return uploadEntriesAsync(entries, handle.name, target, directory, manifest.sha256, { api, signal, progress });
}

/** 転送項目の進捗と取消しを管理しながら送信します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function uploadEntriesAsync(entries, name, target, directory, manifest, { api, signal, progress })
{
    if (!name || /[\\/:]/.test(name) || ['.', '..'].includes(name))
    {
        throw new Error('Invalid file name.');
    }
    const destination = directory.replace(/[\\/]$/, '') + '/' + name;
    const request = { requestId: crypto.randomUUID(), sourcePeer: 'browser', sourcePath: name, destinationPath: destination, manifestSha256: manifest, isDirectory: entries[0].isDirectory, overwrite: false };
    let stage, committing = false, completed = false;
    try
    {
        signal.throwIfAborted();
        stage = await api('/api/browser-files/open', target, request, signal);
        let transferred = 0;
        const total = entries.reduce((sum, entry) => sum + entry.length, 0);
        progress('uploading', 0, total);
        for (const entry of entries)
        {
            signal.throwIfAborted();
            if (entry.isDirectory)
            {
                await api('/api/browser-files/directory', target, { stage, relativePath: entry.relativePath }, signal);
                continue;
            }
            const { file, sha256: digest, relativePath } = entry;
            for (let offset = 0; offset < file.size || offset === 0; offset += 2 * 1024 * 1024)
            {
                signal.throwIfAborted();
                const bytes = new Uint8Array(await file.slice(offset, offset + 2 * 1024 * 1024).arrayBuffer());
                const query = new URLSearchParams({ stage: stage.id, relativePath, offset, count: bytes.length, length: file.size, sha256: digest, partSha256: hex(sha256(bytes)) });
                await api('/client/browser-files/write?' + query, target, bytes, signal);
                transferred += bytes.length;
                progress('uploading', transferred, total);
                if (!file.size)
                {
                    break;
                }
            }
        }
        signal.throwIfAborted();
        committing = true;

        // A lost commit reply is unknown, not a reason to repeat a write.
        const result = await api('/api/browser-files/commit', target, stage, signal);
        completed = true;
        progress('completed', transferred, total);
        return result;
    }
    catch (error)
    {
        if (committing)
        {
            throw new Error('Commit unconfirmed. Refresh the destination before retrying.', { cause: error });
        }
        throw error;
    }
    finally
    {
        if (stage && (!committing || completed))
        {
            try
            {
                await api('/api/browser-files/discard', target, stage, AbortSignal.timeout(5000));
            }
            catch
            { /* 未確認のstageは調査できるよう残し、アップロードを暗黙に再送しません。 */ }
        }
    }
}
