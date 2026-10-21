import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { hashBrowserFile, fileManifest, uploadBrowserFile } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-file-upload.js';

/** 試験データのダイジェストを計算します。
 * @returns 呼出し元が使用する値です。
 */
const digest = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();

const target = { clusterId: 'cluster', peerId: 'peer', instanceId: 'original' };

test('incremental hashing is bounded to 1 MiB and matches SHA256 across chunk boundaries', async() =>
{
    const bytes = new Uint8Array(2 * 1024 * 1024 + 17).fill(73), file = new File([bytes], 'data.bin');
    let maximum = 0;
    const source = { size: file.size,

        /** 指定された範囲の試験データを返します。
         * @returns 呼出し元が使用する値です。
         */
        slice(start, end)
        {
            maximum = Math.max(maximum, end - start);
            return file.slice(start, end);
        } };
    assert.equal(await hashBrowserFile(source, new AbortController().signal), digest(bytes));
    assert.equal(maximum, 1024 * 1024);
    const expected = JSON.stringify([{ RelativePath: '', IsDirectory: false, Length: bytes.length, Sha256: digest(bytes) }]);
    assert.equal(fileManifest(bytes.length, digest(bytes)), digest(expected));
});
for (const length of [0, 80000, 2 * 1024 * 1024 + 17])
{
    test(`upload ${length} bytes pins the target, verifies chunks and discards the completed stage`, async() =>
    {
        const file = new File([new Uint8Array(length).fill(91)], '日本語.bin'), calls = [], received = [];

        /** 取消しと応答の確認を伴う管理要求を送信します。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        const apiAsync = async(path, peer, body) =>
        {
            calls.push(path);
            assert.equal(peer, target);
            if (path.endsWith('/open'))
            {
                assert.equal(body.overwrite, false);
                assert.equal(body.destinationPath, 'C:/target/日本語.bin');
                return { id: 'stage' };
            }
            if (path.startsWith('/client/browser-files/write?'))
            {
                const query = new URL(path, 'http://localhost').searchParams, bytes = Buffer.from(body);
                assert.ok(bytes.length <= 2 * 1024 * 1024);
                assert.equal(Number(query.get('offset')), received.reduce((n, x) => n + x.length, 0));
                assert.equal(query.get('partSha256'), digest(bytes));
                received.push(bytes);
            }
            return true;
        };

        await uploadBrowserFile(file, target, 'C:/target', apiAsync, new AbortController().signal);
        assert.equal(Buffer.concat(received).length, length);
        assert.deepEqual(calls.slice(-2), ['/api/browser-files/commit', '/api/browser-files/discard']);
    });
}
test('cancellation discards a partial stage and does not commit', async() =>
{
    const controller = new AbortController(), calls = [];
    await assert.rejects(uploadBrowserFile(new File(['hello'], 'a.txt'), target, 'C:/target', async path =>
    {
        calls.push(path);
        if (path.endsWith('/open'))
        {
            return { id: 'stage' };
        }
        if (path.startsWith('/client/browser-files/write?'))
        {
            controller.abort();
        }
    }, controller.signal), { name: 'AbortError' });
    assert.equal(calls.at(-1), '/api/browser-files/discard');
    assert.ok(!calls.includes('/api/browser-files/commit'));
});
test('a lost commit reply remains unknown and is never implicitly retried or discarded', async() =>
{
    const calls = [];
    await assert.rejects(uploadBrowserFile(new File(['hello'], 'a.txt'), target, 'C:/target', async path =>
    {
        calls.push(path);
        if (path.endsWith('/open'))
        {
            return { id: 'stage' };
        }
        if (path.endsWith('/commit'))
        {
            throw new Error('connection lost');
        }
    }, new AbortController().signal), /Commit unconfirmed/);
    assert.equal(calls.filter(x => x.endsWith('/commit')).length, 1);
    assert.equal(calls.at(-1), '/api/browser-files/commit');
});
