import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileScopeSearch } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/signal-scope-search.js';
import { ScopeHistory, readScopeFrame } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/signal-scope-history.js';

/** 一つの受信を表す有限の原本です。
 * @returns パケットメタデータです。
 */
function packet(overrides = {})
{
    return { packetId: 1, queryId: 0, matched: true, sourceId: 'pcap:nic1', observationPoint: 'rx', time: '2025-01-02T04:00:00.000Z', fingerprint: 'same-ip-packet',
        source: '192.168.10.10', destination: '192.168.10.255', sourcePort: 9001, destinationPort: 9000, protocol: 'OSC', transport: 'UDP',
        summary: '/address/to ready and or', ...overrides };
}

test('search handles precedence, groups, host names and quoted operator words without evaluating code', () =>
{
    const match = compileScopeSearch('osc and (192.168.10. or 192.168.20.) and "/address/to" and "ready"');
    assert(match('osc 192.168.20.1 /address/to ready'));
    assert.equal(match('osc 192.168.30.1 /address/to ready'), false);
    assert.equal(match('osc 192.168.20.1 /other ready'), false);
    assert(compileScopeSearch('alpha OR beta AND gamma')('alpha'));
    assert.equal(compileScopeSearch('alpha OR beta AND gamma')('beta'), false);
    assert(compileScopeSearch('"and" AND "or"')('a literal and or'));
    assert(compileScopeSearch('"a \\"quote\\""')('a "quote"'));
    assert(compileScopeSearch('server-host "and"')('server-host and'));
    assert(compileScopeSearch('')('anything'));
    for (const query of ['osc AND', '(osc OR udp', 'osc)', '"unterminated', 'AND udp', '""', '('.repeat(34) + 'x' + ')'.repeat(34), 'x'.repeat(2049)])
    {
        assert.throws(() => compileScopeSearch(query), Error);
    }
});

test('cross-host and mixed-backend observations merge one-to-one while genuine repeat transmissions stay separate', () =>
{
    const history = new ScopeHistory();
    history.add(packet(), 'host-a', 0);
    history.add(packet(), 'host-a', 1);
    history.add(packet(), 'host-b', 2);
    history.add(packet(), 'host-b', 3);
    history.add(packet({ sourceId: 'pktmon:nic1', observationPoint: '16/1/3' }), 'host-a', 4);
    history.add(packet({ sourceId: 'pktmon:nic1', observationPoint: '16/1/3' }), 'host-a', 5);
    const view = history.view(0);
    assert.equal(view.retained, 2);
    assert.equal(view.duplicates, 4);
    assert.deepEqual(view.rows.map(row => row.observationCount), [3, 3]);
    history.add(packet({ time: '2025-01-02T04:00:01.000Z' }), 'host-c', 6);
    history.add(packet({ fingerprint: 'different-payload' }), 'host-b', 7);
    assert.equal(history.view(0).retained, 4);
});

test('host names and Info remain searchable locally in both display modes, and invalid expressions preserve the last valid filter', () =>
{
    const history = new ScopeHistory();
    history.setNames([['192.168.10.10', 'server-host']]);
    history.add(packet(), 'observing-host');
    history.search('server-host AND "and or"');
    assert.equal(history.view(0).total, 1);
    assert.equal(history.view(0).rows[0].sourceName, 'server-host');
    assert.throws(() => history.search('server-host AND'));
    assert.equal(history.view(0).total, 1);
    history.search('unknown-host');
    assert.equal(history.view(0).total, 0);
    history.setNames([['192.168.10.10', 'unknown-host']]);
    assert.equal(history.view(0).total, 1);
});

test('late observers can make an older row match without duplicating or reordering displayed rows', () =>
{
    const history = new ScopeHistory();
    history.add(packet({ fingerprint: 'first' }), 'host-a', 0);
    history.add(packet({ fingerprint: 'second' }), 'host-b', 1);
    history.search('host-b');
    assert.deepEqual(history.view(0).rows.map(row => row.id), [2]);
    history.add(packet({ fingerprint: 'first' }), 'host-b', 2);
    assert.deepEqual(history.view(0).rows.map(row => row.id), [1, 2]);
});

test('rolling bounds count and memory, clears expiry references, and exposes only a bounded view', () =>
{
    const history = new ScopeHistory(100, 1048576);
    for (let index = 0; index < 20000; index++)
    {
        history.add(packet({ fingerprint: String(index) }), 'host', index);
    }
    assert.equal(history.view(0).retained, 100);
    assert.equal(history.view(0).rolled, 19900);
    assert.equal(history.view(0).rows[0].id, 19901);
    assert(history.recent.size <= 2001);
    assert(history.expiry.length < 16386);
    const first = history.view(0).rows[0];
    assert.equal('raw' in first, false);
    assert.equal('search' in first, false);
    for (let index = 0; index < 100; index++)
    {
        history.add(packet({ sourceId: 'x'.repeat(4096), summary: 'x'.repeat(512) }), 'host', index + 20000);
    }
    assert(history.bytes <= 1048576);
    assert(history.byId.size < 100);
    history.clear();
    assert.equal(history.view(0).retained, 0);
    assert.equal(history.bytes, 0);
    assert.equal(history.recent.size, 0);
});

test('live frames reject raw contents while requested detail frames keep binary without base64 conversion', () =>
{
    const data = packet(), meta = new TextEncoder().encode(JSON.stringify(data));
    const wire = new Uint8Array(5 + meta.length + 3);
    wire[0] = 2;
    new DataView(wire.buffer).setUint32(1, meta.length);
    wire.set(meta, 5);
    wire.set([0, 128, 255], 5 + meta.length);
    assert.throws(() => readScopeFrame(wire.buffer));
    wire[0] = 3;
    assert.deepEqual(readScopeFrame(wire.buffer).detail.raw, new Uint8Array([0, 128, 255]));
    const summary = wire.slice(0, 5 + meta.length);
    summary[0] = 2;
    assert.equal('raw' in readScopeFrame(summary.buffer).packet, false);
    new DataView(wire.buffer).setUint32(1, 2000000);
    assert.throws(() => readScopeFrame(wire.buffer));
    assert.throws(() => readScopeFrame(new ArrayBuffer(1)));
    assert.throws(() => readScopeFrame(new Uint8Array([9, 1]).buffer));
});

test('advanced source matches combine with local search and ignore stale replies', () =>
{
    const history = new ScopeHistory();
    history.add(packet({ packetId: 5 }), 'host-a', 0, 'connection-a');
    history.add(packet({ packetId: 7 }), 'host-b', 0, 'connection-b');
    history.add(packet({ packetId: 8, fingerprint: 'different' }), 'host-a', 1, 'connection-a');
    history.setAdvanced(10);
    assert.equal(history.view(0).total, 0);
    history.applyAdvanced('connection-b', 9, new Set([7]));
    assert.equal(history.view(0).total, 0);
    history.applyAdvanced('connection-b', 10, new Set([7]));
    assert.equal(history.view(0).total, 1);
    history.search('unknown');
    assert.equal(history.view(0).total, 0);
    history.search('host-b');
    assert.equal(history.view(0).total, 1);
    history.setAdvanced(0);
    history.search('');
    assert.equal(history.view(0).total, 2);
});

test('browsed rows stay anchored through rolling and a zero window disables observation merging', () =>
{
    const history = new ScopeHistory(100, 1048576, 0);
    for (let index = 0; index < 100; index++)
    {
        history.add(packet(), index % 2 ? 'host-a' : 'host-b', index);
    }
    assert.equal(history.duplicates, 0);
    const anchor = history.view(40, 20).rows[0].id;
    for (let index = 100; index < 130; index++)
    {
        history.add(packet(), 'host-a', index);
    }
    const anchored = history.view(40, 20, false, anchor);
    assert.equal(anchored.first, 10);
    assert.equal(anchored.rows[0].id, anchor);
});

test('fifty thousand retained packets do not require a full-history scan for each repeated observation', context =>
{
    const history = new ScopeHistory();
    const started = performance.now();
    for (let index = 0; index < 50000; index++)
    {
        history.add(packet(), 'host-a', index / 50);
        history.add(packet(), 'host-b', index / 50);
    }
    const elapsed = performance.now() - started;
    assert.equal(history.byId.size, 50000);
    assert.equal(history.duplicates, 50000);
    assert(history.expiry.length - history.expiryHead <= 8192);
    assert.equal(history.view(0, 80, true).rows.length, 80);
    context.diagnostic(`100000 observations / 50000 retained: ${elapsed.toFixed(1)} ms; estimated ${(history.bytes / 1048576).toFixed(1)} MiB`);
    assert(elapsed < 15000, 'Repeated observations should remain bounded under a sustained burst.');
});
