import { test } from 'node:test';
import assert from 'node:assert/strict';
import { listLocalFiles, localDirectory, downloadToLocal } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-local-files.js';
import { uploadBrowserDirectory } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-file-upload.js';
import { LocalFile, LocalFolder } from './fixtures/local-files.mjs';
const target = { clusterId: 'cluster', peerId: 'peer', instanceId: 'original' };

/** 試験に使用する取消信号を返します。
 * @returns 呼出し元が使用する値です。
 */
const signal = () => new AbortController().signal;

const source = { name: 'a.bin', path: 'C:/source/a.bin', isDirectory: false };

/** 試験用のダウンロード要求と内容を記録します。
 * @returns 呼出し元が使用する値です。
 */
const download = (bytes = 'abc', length = 3) => new Response(bytes, { headers: { 'content-length': String(length) } });

test('permission denial never enumerates and navigation cannot leave the chosen directory', async() =>
{
    const folder = new LocalFolder('chosen', [new LocalFolder('child', [new LocalFile('a')])]);
    folder.permission = 'denied';
    await assert.rejects(listLocalFiles(folder, '', signal()), /not granted/);
    assert.equal(folder.enumerations, 0);
    folder.permission = 'granted';
    await assert.rejects(localDirectory(folder, '../outside'), /inside/);
    const listed = await listLocalFiles(folder, 'child', signal());
    assert.equal(listed.entries[0].path, 'child/a');
});

test('directory upload preserves empty folders, Unicode paths and one atomic commit', async() =>
{
    const root = new LocalFolder('upload', [new LocalFolder('日本語', [new LocalFile('a.txt', 'abc')]), new LocalFolder('empty')]);
    const calls = [], written = [];
    let manifest;
    await uploadBrowserDirectory(root, target, 'C:/target', async(path, peer, body) =>
    {
        calls.push(path);
        if (path.endsWith('/manifest'))
        {
            manifest = body;
            assert.ok(body.every(entry => !('file' in entry)));
            return { sha256: 'expected' };
        }
        assert.equal(peer, target);
        if (path.endsWith('/open'))
        {
            assert.equal(body.isDirectory, true);
            assert.equal(body.overwrite, false);
            assert.equal(body.manifestSha256, 'expected');
            return { id: 'stage' };
        }
        if (path.startsWith('/client/browser-files/write?'))
        {
            written.push(new URL(path, 'http://localhost').searchParams.get('relativePath'));
        }
        return true;
    }, signal());
    assert.deepEqual(manifest.map(item => item.relativePath), ['', '日本語', '日本語/a.txt', 'empty']);
    assert.deepEqual(written, ['日本語/a.txt']);
    assert.equal(calls.filter(path => path.endsWith('/directory')).length, 3);
    assert.deepEqual(calls.slice(-2), ['/api/browser-files/commit', '/api/browser-files/discard']);
});

test('directory upload cancellation does not commit and releases only the staged data', async() =>
{
    const controller = new AbortController(), calls = [];
    await assert.rejects(uploadBrowserDirectory(new LocalFolder('empty'), target, 'C:/target', async path =>
    {
        calls.push(path);
        if (path.endsWith('/manifest'))
        {
            return { sha256: 'hash' };
        }
        if (path.endsWith('/open'))
        {
            return { id: 'stage' };
        }
        if (path.endsWith('/directory'))
        {
            controller.abort();
        }
        return true;
    }, controller.signal), { name: 'AbortError' });
    assert.equal(calls.at(-1), '/api/browser-files/discard');
    assert.ok(!calls.includes('/api/browser-files/commit'));
});

test('download streams into a new permitted file and closes it exactly once', async() =>
{
    const folder = new LocalFolder('target');
    const urls = [];
    assert.equal(await downloadToLocal(source, folder, target, () =>
    {}, signal(), { progress: () =>
    {},

    fetchFile: async url =>
    {
        urls.push(url);
        return download();
    } }), 3);
    const file = folder.children.get('a.bin');
    assert.equal(await (await file.getFile()).text(), 'abc');
    assert.equal(file.closes, 1);
    assert.equal(file.aborts, 0);
    assert.match(urls[0], /instanceId=original/);
});

test('same-name file, directory and denied permission cannot be overwritten', async() =>
{
    for (const existing of [new LocalFile('a.bin', 'keep'), new LocalFolder('a.bin')])
    {
        const folder = new LocalFolder('target', [existing]);
        let fetched = false;
        await assert.rejects(downloadToLocal(source, folder, target, () =>
        {}, signal(), { progress: () =>
        {},

        fetchFile: async() =>
        {
            fetched = true;
            return download();
        } }));
        assert.equal(fetched, false);
        assert.equal(folder.children.get('a.bin'), existing);
    }
    const folder = new LocalFolder('target');
    folder.permission = 'denied';
    await assert.rejects(downloadToLocal(source, folder, target, () =>
    {}, signal()), /not granted/);
    assert.equal(folder.children.size, 0);
});

test('truncation and cancellation abort local writes without claiming completion', async() =>
{
    const folder = new LocalFolder('target');
    await assert.rejects(downloadToLocal(source, folder, target, () =>
    {}, signal(), { progress: () =>
    {},

    fetchFile: async() => download('x', 3) }), /incomplete/);
    const file = folder.children.get('a.bin');
    assert.equal(file.closes, 0);
    assert.equal(file.aborts, 1);
    const next = new LocalFolder('other'), controller = new AbortController();
    await assert.rejects(downloadToLocal(source, next, target, () =>
    {}, controller.signal, { progress: () => controller.abort(),

        fetchFile: async() => download() }), { name: 'AbortError' });
    assert.equal(next.children.get('a.bin').aborts, 1);
});

test('an unconfirmed local close is not repeated or deleted', async() =>
{
    const folder = new LocalFolder('target');
    folder.created = file =>
    {
        const assignedValue = file.closeError = new Error('lost close');
        return assignedValue;
    };
    await assert.rejects(downloadToLocal(source, folder, target, () =>
    {}, signal(), { progress: () =>
    {},

    fetchFile: async() => download() }), /save result is unconfirmed/);
    const file = folder.children.get('a.bin');
    assert.equal(file.closes, 1);
    assert.equal(file.aborts, 0);
    assert.equal(folder.children.size, 1);
});

test('folder download preserves empty directories and rejects escaped source paths before writes', async() =>
{
    const entry = { name: 'folder', path: 'C:/folder', isDirectory: true }, folder = new LocalFolder('target');

    /** 取消しと応答の確認を伴う管理要求を送信します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const apiAsync = async(path, peer, body) => ({ path: body.path, entries: body.path === 'C:/folder' ? [{ name: 'empty', path: 'C:/folder/empty', isDirectory: true }, { ...source, path: 'C:/folder/a.bin' }] : [] });

    await downloadToLocal(entry, folder, target, apiAsync, signal(), { progress: () =>
    {},

    fetchFile: async() => download() });
    const copied = folder.children.get('folder');
    assert.equal(copied.children.get('empty').kind, 'directory');
    assert.equal(await (await copied.children.get('a.bin').getFile()).text(), 'abc');
    const other = new LocalFolder('other');
    await assert.rejects(downloadToLocal(entry, other, target, async() => ({ path: entry.path, entries: [{ ...source, path: 'C:/outside/a.bin' }] }), signal()), /outside/);
    assert.equal(other.children.size, 0);
});
