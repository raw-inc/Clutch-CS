import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validFolderName, mutateHostFileAsync, createLocalFolderAsync, deleteLocalEntryAsync } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/file-mutations.js';
import { listLocalFiles } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/browser-local-files.js';
import { LocalFile, LocalFolder } from './fixtures/local-files.mjs';

test('folder names allow Unicode and spaces without permitting traversal, reserved names or control characters', () =>
{
    for (const name of ['日本語 フォルダー', '.hidden', 'new.folder', 'CONtent'])
    {
        assert.equal(validFolderName(name), true, name);
    }
    for (const name of ['', ' ', '.', '..', '../escape', 'child/name', 'C:\\path', 'a\u0000b', 'a\nb', 'a?', 'a.', 'a ', 'CON', 'com1.txt', 'LPT9', 'a'.repeat(256)])
    {
        assert.equal(validFolderName(name), false, name);
    }
});

test('host changes use the pinned instance and reject changed discovery before dispatch', async() =>
{
    const peer = { clusterId: 'cluster', peerId: 'peer', instanceId: crypto.randomUUID(), status: 'ready' };
    let peers = [peer];
    const requests = [];
    const ctx = { peers: async() => peers,

        t: text => text,

        api: async(path, target, body) =>
        {
            requests.push(body);
            assert.equal(path, '/api/nodes/execute');
            assert.equal(target, peer);
            return { outcome: 'succeeded' };
        } };
    await mutateHostFileAsync(ctx, peer, 'directory.delete', 'C:/chosen/folder');
    assert.equal(requests[0].targetInstanceId, peer.instanceId);
    assert.equal(requests[0].targetPeerId, peer.peerId);
    assert.deepEqual(requests[0].node.settings, { path: 'C:/chosen/folder', recursive: true });
    await mutateHostFileAsync(ctx, peer, 'directory.create', 'C:/chosen/new');
    assert.deepEqual(requests[1].node.settings, { path: 'C:/chosen/new', existingFileMode: 'error' });
    peers = [{ ...peer, instanceId: crypto.randomUUID() }];
    await assert.rejects(mutateHostFileAsync(ctx, peer, 'file.delete', 'C:/chosen/file'), /changed/);
    assert.equal(requests.length, 2);
});

test('unknown or failed host responses are reported without retrying the mutation', async() =>
{
    const peer = { clusterId: 'cluster', peerId: 'peer', instanceId: crypto.randomUUID(), status: 'ready' };
    for (const outcome of ['unknown', 'failed', 'notExecuted', 'cancelled'])
    {
        let calls = 0;
        const ctx = { peers: async() => [peer],

            t: text => text,

            api: async() =>
            {
                calls++;
                return { outcome, message: 'Unconfirmed or rejected' };
            } };
        await assert.rejects(mutateHostFileAsync(ctx, peer, 'directory.create', 'C:/chosen/new'), /Unconfirmed/);
        assert.equal(calls, 1);
    }
});

test('local creation stays inside the chosen folder and never adopts an existing file or directory', async() =>
{
    const root = new LocalFolder('root', [new LocalFolder('child'), new LocalFile('file')]);
    await createLocalFolderAsync(root, 'child', '日本語');
    assert.ok(root.children.get('child').children.has('日本語'));
    await assert.rejects(createLocalFolderAsync(root, '', 'child'));
    await assert.rejects(createLocalFolderAsync(root, '', 'file'));
    await assert.rejects(createLocalFolderAsync(root, '../outside', 'new'), /inside/);
    await assert.rejects(createLocalFolderAsync(root, '', '../outside'), /Invalid/);
    root.permission = 'denied';
    await assert.rejects(createLocalFolderAsync(root, '', 'denied'), /not granted/);
    assert.equal(root.children.size, 2);
});

test('local deletion removes selected files and folder contents while preserving unselected entries', async() =>
{
    const root = new LocalFolder('root', [new LocalFolder('folder', [new LocalFile('nested')]), new LocalFile('file'), new LocalFile('keep')]);
    const listing = await listLocalFiles(root, '', new AbortController().signal);
    for (const entry of listing.entries.filter(entry => entry.name !== 'keep'))
    {
        await deleteLocalEntryAsync(root, '', entry);
    }
    assert.deepEqual([...root.children.keys()], ['keep']);
});

test('local deletion rejects replaced handles and revoked permission before modifying entries', async() =>
{
    const root = new LocalFolder('root', [new LocalFile('file')]);
    const { entries } = await listLocalFiles(root, '', new AbortController().signal);
    root.children.set('file', new LocalFile('file', 'replacement'));
    await assert.rejects(deleteLocalEntryAsync(root, '', entries[0]), /changed/);
    assert.equal(root.children.get('file').contents, 'replacement');
    root.permission = 'denied';
    await assert.rejects(deleteLocalEntryAsync(root, '', entries[0]), /not granted/);
    assert.equal(root.children.size, 1);
});

test('leaving the view during local validation prevents a later creation or deletion', async() =>
{
    const root = new LocalFolder('root', [new LocalFile('keep')]);
    const { entries } = await listLocalFiles(root, '', new AbortController().signal);
    const controller = new AbortController();
    root.queryPermission = async() =>
    {
        controller.abort();
        return 'granted';
    };
    await assert.rejects(createLocalFolderAsync(root, '', 'cancelled', controller.signal), { name: 'AbortError' });
    await assert.rejects(deleteLocalEntryAsync(root, '', entries[0], controller.signal), { name: 'AbortError' });
    assert.deepEqual([...root.children.keys()], ['keep']);
});
