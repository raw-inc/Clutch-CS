import { currentPeer } from './file-transfer-selection.js';
import { localDirectory, requireLocalPermission, requireLocalAbsenceAsync } from './browser-local-files.js';

/** 新しいフォルダー名を一階層の移植可能な名前へ限定します。
 * @returns 親移動・絶対パス・予約名を含まないかです。
 */
export function validFolderName(name)
{
    return typeof name === 'string' && !!name.trim() && name.length <= 255
        && !/[\\/:*?"<>|]/u.test(name) && !/[. ]$/u.test(name) && [...name].every(character => 32 <= character.codePointAt(0))
        && !['.', '..'].includes(name) && !/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$/iu.test(name);
}

/** 固定したPCで、既存のファイルノードと同じ許可・排他・復元方針を使用します。
 * @returns 一回の操作の確認済み結果です。未確認時は再送しません。
 */
export async function mutateHostFileAsync(ctx, peer, operation, path)
{
    if (!currentPeer(await ctx.peers(), peer))
    {
        throw new Error(ctx.t('The selected PC changed or is unavailable. Refresh hosts before trying again.', '選択したPCが変更されたか、利用できません。ホスト一覧を更新してください。'));
    }
    ctx.signal?.throwIfAborted();
    const id = crypto.randomUUID(), settings = { path };
    const directoryCreate = 'directory.create', directoryDelete = 'directory.delete';
    if (operation === directoryCreate)
    {
        settings.existingFileMode = 'error';
    }
    if (operation === directoryDelete)
    {
        settings.recursive = true;
    }
    const request = { id, causationId: id, workflowId: '$file-transfer', revision: 'file-transfer-v1',
        node: { id: operation, type: operation, settings }, inputs: {}, targetPeerId: peer.peerId, targetInstanceId: peer.instanceId,
        expiresAt: new Date(Date.now() + 300000).toISOString() };
    const result = await ctx.api('/api/nodes/execute', peer, request);
    const succeeded = 'succeeded';
    if (result?.outcome !== succeeded)
    {
        throw new Error(result?.message || result?.reasonCode || ctx.t('The file operation was not confirmed. Refresh before repeating it.', 'ファイル操作の結果を確認できません。繰り返す前に一覧を更新してください。'));
    }
}

/** 選択フォルダーの直下へ作成し、同名のファイルやフォルダーを採用しません。
 * @returns ブラウザーが所有するフォルダーの作成完了です。
 */
export async function createLocalFolderAsync(root, path, name, signal)
{
    if (!validFolderName(name))
    {
        throw new Error('Invalid folder name.');
    }
    const parent = await localDirectory(root, path);
    await requireLocalPermission(parent, 'readwrite');
    await requireLocalAbsenceAsync(parent, name);
    signal?.throwIfAborted();
    await parent.getDirectoryHandle(name, { create: true });
}

/** 一覧に表示した実体が置き換わっていない場合だけ、確認済みの項目を削除します。
 * @returns 選択したローカル項目の削除完了です。
 */
export async function deleteLocalEntryAsync(root, path, entry, signal)
{
    const parent = await localDirectory(root, path);
    await requireLocalPermission(parent, 'readwrite');
    const current = entry.isDirectory ? await parent.getDirectoryHandle(entry.name) : await parent.getFileHandle(entry.name);
    if (!entry.handle || !await current.isSameEntry(entry.handle))
    {
        throw new Error('The selected entry changed. Refresh the folder before deleting it.');
    }
    signal?.throwIfAborted();
    await parent.removeEntry(entry.name, { recursive: entry.isDirectory });
}
