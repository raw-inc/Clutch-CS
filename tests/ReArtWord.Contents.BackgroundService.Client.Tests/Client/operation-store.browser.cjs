// Run with node --test; set CONTENTS_TEST_PLAYWRIGHT to an installed Playwright package if needed.
// Uses an isolated browser profile and a loopback fixture, never an operator's stored data.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { chromium } = require(process.env.CONTENTS_TEST_PLAYWRIGHT || 'playwright');
const source = readFileSync(join(__dirname, '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/operation-store.js'));

test('native browser operation storage', async t =>
{
    const server = createServer((request, response) =>
    {
        response.setHeader('Content-Type', request.url === '/operation-store.js' ? 'text/javascript' : 'text/html');
        response.end(request.url === '/operation-store.js' ? source : '<script src="/operation-store.js"></script>');
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    t.after(() => new Promise(resolve =>
    {
        server.close(resolve);
    }));
    const browser = await chromium.launch({ channel: process.env.CONTENTS_TEST_BROWSER || 'msedge', headless: true });
    t.after(() => browser.close());
    const url = `http://127.0.0.1:${server.address().port}`;

    /** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    async function fixtureAsync(sub)
    {
        const context = await browser.newContext();
        sub.after(() => context.close());
        const page = await context.newPage();
        await page.goto(url);
        await page.evaluate(() =>
        {
            window.record = (id = crypto.randomUUID(), size = 0) => ({
                operationId: id, kind: 'restore', readPath: '/api/snapshots/restores/' + id,
                recoverPath: '/api/snapshots/recover', request: { operationId: id, selections: ['original'], data: 'x'.repeat(size) },
                createdAt: new Date().toISOString(), result: null, peerId: 'original-peer', clusterId: 'zone2-cluster'
            });
        });
        return { context, page };
    }

    await t.test('full localStorage migrates intact and large results survive reload', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const migrated = await page.evaluate(async () =>
        {
            const items = Array.from({ length: 3 }, () => window.record(undefined, 1400000));
            const older = window.record();
            localStorage.setItem('contents.blazor.operations', JSON.stringify(items));
            localStorage.setItem('contents.operations', JSON.stringify([older]));
            localStorage.setItem('contents.blazor.draft.example', 'unsaved draft');
            localStorage.setItem('contents.blazor.language', 'ja');
            let failure;
            try
            {
                localStorage.setItem('contents.blazor.operations', JSON.stringify([...items, window.record(undefined, 2000000)]));
            }
            catch (error)
            {
                failure = error.name;
            }
            const saved = JSON.parse(await window.contentsOperationStore.read());
            const migrated = saved.find(item => item.operationId === items[0].operationId);
            const large = window.record();
            await window.contentsOperationStore.write(JSON.stringify(large), 'create');
            large.result = { operationId: large.operationId, outcome: 'succeeded', payload: '音声'.repeat(3 * 1024 * 1024) };
            await window.contentsOperationStore.write(JSON.stringify(large), 'update');
            return {
                failure, count: saved.length, same: JSON.stringify(migrated) === JSON.stringify(items[0]),
                largeId: large.operationId, oldKeysRemoved: !localStorage.getItem('contents.blazor.operations') && !localStorage.getItem('contents.operations'),
                draft: localStorage.getItem('contents.blazor.draft.example'), language: localStorage.getItem('contents.blazor.language')
            };
        });
        assert.equal(migrated.failure, 'QuotaExceededError');
        assert.equal(migrated.count, 4);
        assert.equal(migrated.same, true);
        assert.equal(migrated.oldKeysRemoved, true);
        assert.equal(migrated.draft, 'unsaved draft');
        assert.equal(migrated.language, 'ja');
        await page.reload();
        const restored = await page.evaluate(async id =>
        {
            const saved = JSON.parse(await window.contentsOperationStore.read());
            const large = saved.find(item => item.operationId === id);
            return { count: saved.length, length: large.result.payload.length, peer: large.peerId, recovery: large.recoverPath };
        }, migrated.largeId);
        assert.equal(restored.count, 5);
        assert.equal(restored.length, 6 * 1024 * 1024);
        assert.equal(restored.peer, 'original-peer');
        assert.equal(restored.recovery, '/api/snapshots/recover');
    });

    await t.test('migration abort keeps the source and retries without partial import', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const original = JSON.stringify([window.record(), window.record()]);
            localStorage.setItem('contents.blazor.operations', original);
            const add = IDBObjectStore.prototype.add;
            IDBObjectStore.prototype.add = function (...args)
            {
                const request = add.apply(this, args);
                request.addEventListener('success', () => this.transaction.abort());
                return request;
            };
            let rejected = false;
            try
            {
                await window.contentsOperationStore.read();
            }
            catch
            {
                rejected = true;
            }
            finally
            {
                IDBObjectStore.prototype.add = add;
            }
            const preserved = localStorage.getItem('contents.blazor.operations') === original;
            const count = await new Promise((resolve, reject) =>
            {
                const request = indexedDB.open('contents.operation-journal', 1);
                request.onerror = () => reject(request.error);
                request.onsuccess = () =>
                {
                    const database = request.result, tx = database.transaction('operations'), count = tx.objectStore('operations').count();
                    tx.oncomplete = () =>
                    {
                        database.close();
                        resolve(count.result);
                    };
                };
            });
            const retried = JSON.parse(await window.contentsOperationStore.read());
            return { rejected, preserved, count, retried: retried.length };
        });
        assert.deepEqual(result, { rejected: true, preserved: true, count: 0, retried: 2 });
    });

    await t.test('request success followed by transaction abort is not reported as saved', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            await window.contentsOperationStore.read();
            const add = IDBObjectStore.prototype.add;
            let requestSucceeded = false, rejected = false;
            IDBObjectStore.prototype.add = function (...args)
            {
                const request = add.apply(this, args);
                request.addEventListener('success', () =>
                {
                    requestSucceeded = true;
                    this.transaction.abort();
                });
                return request;
            };
            try
            {
                await window.contentsOperationStore.write(JSON.stringify(window.record()), 'create');
            }
            catch
            {
                rejected = true;
            }
            finally
            {
                IDBObjectStore.prototype.add = add;
            }
            return { requestSucceeded, rejected, count: JSON.parse(await window.contentsOperationStore.read()).length };
        });
        assert.deepEqual(result, { requestSucceeded: true, rejected: true, count: 0 });
    });

    await t.test('an old tab changing the source during migration is not erased', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const first = window.record(), second = window.record();
            localStorage.setItem('contents.blazor.operations', JSON.stringify([first]));
            const add = IDBObjectStore.prototype.add;
            IDBObjectStore.prototype.add = function (...args)
            {
                localStorage.setItem('contents.blazor.operations', JSON.stringify([first, second]));
                return add.apply(this, args);
            };
            try
            {
                await window.contentsOperationStore.read();
            }
            finally
            {
                IDBObjectStore.prototype.add = add;
            }
            const kept = !!localStorage.getItem('contents.blazor.operations');
            const reloaded = JSON.parse(await window.contentsOperationStore.read());
            return { kept, count: reloaded.length, removed: localStorage.getItem('contents.blazor.operations') === null };
        });
        assert.deepEqual(result, { kept: true, count: 2, removed: true });
    });

    await t.test('two tabs persist independently and old import cannot overwrite a confirmed result', async sub =>
    {
        const { context, page } = await fixtureAsync(sub);
        const other = await context.newPage();
        await other.goto(url);
        const inputs = await page.evaluate(() => Array.from({ length: 40 }, () => window.record()));

        /** 指定された値を所有する保存先へ書き込みます。
         * @returns 呼出し元が使用する値です。
         */
        const write = (target, records) => target.evaluate(async rows => Promise.all(rows.map(row => window.contentsOperationStore.write(JSON.stringify(row), 'create'))), records);

        await Promise.all([write(page, inputs.slice(0, 20)), write(other, inputs.slice(20))]);
        const result = await page.evaluate(async original =>
        {
            const changed = { ...original, peerId: 'wrong-peer', request: {}, result: { outcome: 'succeeded' } };
            const updated = JSON.parse(await window.contentsOperationStore.write(JSON.stringify(changed), 'update'));
            const tracked = JSON.parse(await window.contentsOperationStore.write(JSON.stringify(changed), 'track'));
            let duplicate = false;
            try
            {
                await window.contentsOperationStore.write(JSON.stringify(original), 'create');
            }
            catch
            {
                duplicate = true;
            }
            localStorage.setItem('contents.blazor.operations', JSON.stringify([original]));
            const read = JSON.parse(await window.contentsOperationStore.read());
            return { count: read.length, duplicate, same: JSON.stringify(updated) === JSON.stringify(tracked), peer: tracked.peerId,
                request: tracked.request, outcome: read.find(row => row.operationId === original.operationId).result.outcome };
        }, inputs[0]);
        assert.equal(result.count, 40);
        assert.equal(result.duplicate, true);
        assert.equal(result.same, true);
        assert.equal(result.peer, inputs[0].peerId);
        assert.deepEqual(result.request, inputs[0].request);
        assert.equal(result.outcome, 'succeeded');
    });

    await t.test('invalid legacy data is kept without a partial migration', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const source = JSON.stringify([window.record(), { operationId: 'invalid' }]);
            localStorage.setItem('contents.blazor.operations', source);
            localStorage.setItem('unrelated', 'preserve');
            let rejected = false;
            try
            {
                await window.contentsOperationStore.read();
            }
            catch
            {
                rejected = true;
            }
            return { rejected, same: localStorage.getItem('contents.blazor.operations') === source, other: localStorage.getItem('unrelated') };
        });
        assert.deepEqual(result, { rejected: true, same: true, other: 'preserve' });
    });
});
