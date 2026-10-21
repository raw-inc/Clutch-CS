import test from 'node:test';
import assert from 'node:assert/strict';
import { createReader } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/host-manager/loader.js';

/** 試験対象のホスト情報を作成します。
 * @returns targetを含む結果です。
 */
const host = index => ({ target: { hostId: String(index) } });

/** 経過時間に応じて状態と期限を更新します。
 * @returns 呼出し元が使用する値です。
 */
const tick = () => new Promise(resolve =>
{
    setImmediate(resolve);
});

test('host reads are bounded to three and a failed host does not discard successful hosts', async () =>
{
    let active = 0, peak = 0;
    const controller = new AbortController(), updates = [];
    const ctx = { signal: controller.signal,

        /** 保存された値または現在の応答を読み取ります。
         * @returns 処理の完了と結果を伝えるPromiseです。
         */
        async read(host)
        {
            active++;
            peak = Math.max(peak, active);
            await tick();
            active--;
            if (host.target.hostId === '2')
            {
                throw new Error('offline');
            }
            return { data: host.target.hostId };
        } };
    const reader = createReader(ctx, [0, 1, 2, 3, 4].map(host), () => ({ area: 'network' }), states => updates.push([...states.values()].filter(state => state.loading).length));
    await reader.load();
    assert.equal(peak, 3);
    assert.equal(reader.states.get('2').error, 'offline');
    assert.equal([...reader.states.values()].filter(state => state.snapshot).length, 4);
    assert.equal(updates.at(-1), 0);
    reader.dispose();
});
test('a superseded or disposed read cannot overwrite a new snapshot even when transport ignores abort', async () =>
{
    const controller = new AbortController(), pending = [], values = [];
    const ctx = { signal: controller.signal,

        read: (_, query, signal) => new Promise(resolve =>
        {
            pending.push({ resolve, signal });
        }) };
    const reader = createReader(ctx, [host(1)], () => ({ area: 'hosts' }), states => values.push(states.get('1').snapshot?.data));
    const first = reader.load(), second = reader.load();
    assert.equal(pending[0].signal.aborted, true);
    pending[1].resolve({ data: 'new' });
    await second;
    pending[0].resolve({ data: 'old' });
    await first;
    assert.equal(reader.states.get('1').snapshot.data, 'new');
    assert.equal(values.includes('old'), false);
    const third = reader.load();
    reader.dispose();
    const before = values.length;
    pending[2].resolve({ data: 'disposed' });
    await third;
    assert.equal(pending[2].signal.aborted, true);
    assert.equal(values.length, before);
});
