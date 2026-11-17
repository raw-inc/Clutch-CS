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
    const started = performance.now();
    if (!currentPeer(await ctx.peers(), peer))
    {
        throw new Error(ctx.t('The selected PC changed or is unavailable. Refresh hosts before trying again.', '選択したPCが変更されたか、利用できません。ホスト一覧を更新してください。'));
    }
    ctx.signal?.throwIfAborted();
    const admission = await prepareMutationClockAsync(ctx, peer, started);
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
        expiresAt: admission.expiresAt };
    const result = await ctx.api('/api/v2/nodes/execute-clocked', peer, { request, timing: admission.timing }, ctx.signal);
    const succeeded = 'succeeded';
    if (result?.outcome !== succeeded)
    {
        throw new Error(result?.message || result?.reasonCode || ctx.t('The file operation was not confirmed. Refresh before repeating it.', 'ファイル操作の結果を確認できません。繰り返す前に一覧を更新してください。'));
    }
}

/** 操作準備にかかった時間も差し引き、ClientHostの時計へ初回の受付期限を固定します。
 * @returns 本文に記載する期限と、UTCの補正に影響されない受付情報です。
 */
async function prepareMutationClockAsync(ctx, peer, started)
{
    const clock = await ctx.api('/api/request-clock', peer, undefined, ctx.signal);
    const remaining = Math.floor(300000 - (performance.now() - started));
    const observed = Date.parse(clock?.observedAt);
    const startByTicks = clock?.elapsedTicks + remaining * 10000;
    if (!clock?.instanceId || !Number.isSafeInteger(clock.elapsedTicks) || clock.elapsedTicks < 0 || !Number.isSafeInteger(startByTicks) || !Number.isFinite(observed) || remaining <= 0)
    {
        throw new Error(ctx.t('The operation could not be prepared. Refresh the connection and try again.', '操作の送信準備ができませんでした。接続を更新してから、もう一度お試しください。'));
    }

    // 最初に取得したClientHostの時計情報から期限を決めます。UTCの補正や同じ操作の再送で受付期間を延長しません。
    return { expiresAt: new Date(observed + remaining).toISOString(),
        timing: { instanceId: clock.instanceId, startByTicks, sourceNow: new Date(observed).toISOString() } };
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
