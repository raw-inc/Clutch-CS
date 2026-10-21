import { test } from 'node:test';
import assert from 'node:assert/strict';
import { hostIds, resolveHosts, currentPeer } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/file-transfer-selection.js';

/** 試験に必要なピアの識別情報を作成します。
 * @returns 呼出し元が使用する値です。
 */
const peer = (clusterId, hostId, peerId, priority = 0, status = 'ready') => ({ clusterId, hostId, peerId, priority, status, instanceId: peerId + '-instance', readySince: '2026-01-01T00:00:00Z' });

test('hosts are distinct PCs and explicit clusters constrain their candidates', () =>
{
    const peers = [peer('a', 'left', 'p1'), peer('a', 'left', 'p2'), peer('b', 'right', 'p3')];
    assert.deepEqual(hostIds(peers), ['left', 'right']);
    assert.deepEqual(hostIds(peers, 'a'), ['left']);
    assert.deepEqual(hostIds(peers, 'missing'), []);
});

test('automatic cluster is common to both hosts, independent of discovery ordering', () =>
{
    const peers = [peer('a', 'left', 'one'), peer('b', 'left', 'two'), peer('b', 'right', 'three')];
    for (const ordered of [peers, [...peers].reverse()])
    {
        const selected = resolveHosts(ordered, ['left', 'right']);
        assert.equal(selected.clusterId, 'b');
        assert.deepEqual(selected.peers.map(item => item.peerId), ['two', 'three']);
    }
    assert.equal(resolveHosts(peers, ['left', 'right'], 'a').problem, true);
    assert.equal(resolveHosts(peers, ['left', 'right'], 'missing').problem, true);
});

test('multiple common clusters and multiple peers have stable priorities without duplicate copies', () =>
{
    const peers = [peer('b', 'left', 'b'), peer('b', 'right', 'r'), peer('a', 'left', 'low', 1), peer('a', 'left', 'high', 9), peer('a', 'right', 'right')];
    const selected = resolveHosts(peers, ['left', 'right']);
    assert.equal(selected.clusterId, 'a');
    assert.deepEqual(selected.peers.map(item => item.peerId), ['high', 'right']);
    assert.equal(resolveHosts(peers, ['left', 'right'], 'b').clusterId, 'b');
});

test('unavailable hosts and conflicting peer instances cannot become transfer endpoints', () =>
{
    const live = peer('a', 'left', 'one');
    assert.equal(resolveHosts([live, peer('a', 'right', 'two', 0, 'syncing')], ['left', 'right']).problem, true);
    const conflict = { ...live, instanceId: 'replacement' };
    assert.equal(currentPeer([live, conflict], live), false);
    assert.equal(resolveHosts([live, conflict], ['left', '']).problem, true);
    assert.equal(currentPeer([conflict], live), false);
    assert.equal(resolveHosts([live, live], ['left', '']).peers[0].instanceId, live.instanceId);
});
