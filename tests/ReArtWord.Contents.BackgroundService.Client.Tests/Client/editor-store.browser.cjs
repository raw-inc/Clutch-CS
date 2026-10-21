// Native IndexedDB/Web Locks behavior in an isolated browser profile; no operator data is accessed.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { chromium } = require(process.env.CONTENTS_TEST_PLAYWRIGHT || 'playwright');
const source = readFileSync(join(__dirname, '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/editor-store.js'));

test('native editor storage', async t =>
{
    const server = createServer((request, response) =>
    {
        response.setHeader('Content-Type', request.url === '/editor-store.js' ? 'text/javascript' : 'text/html');
        response.end(request.url === '/editor-store.js' ? source : '<script src="/editor-store.js"></script>');
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
        return { context, page };
    }

    await t.test('migration cleanup removes only unchanged legacy drafts', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const key = 'contents.draft.migration', store = window.contentsEditorStore;
            await store.write(key, 'old');
            const wrong = await store.removeIfUnchanged(key, 'different');
            const kept = await store.read(key);
            await store.write(key, 'concurrent edit');
            const raced = await store.removeIfUnchanged(key, 'old');
            const newer = await store.read(key);
            const removed = await store.removeIfUnchanged(key, 'concurrent edit');
            return { wrong, kept, raced, newer, removed, final: await store.read(key) };
        });
        assert.deepEqual(result, { wrong: false, kept: 'old', raced: false, newer: 'concurrent edit', removed: true, final: null });
    });

    await t.test('unchanged editor values avoid puts but external replacement and deletion are repaired', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const key = 'contents.blazor.draft.write-comparison', text = 'same '.repeat(65536);
            const put = IDBObjectStore.prototype.put;
            let writes = 0;
            IDBObjectStore.prototype.put = function (...args)
            {
                if (args[1] === key)
                {
                    writes++;
                }
                return put.apply(this, args);
            };
            try
            {
                await window.contentsEditorStore.write(key, text);
                await window.contentsEditorStore.write(key, text);
                const identicalWrites = writes;
                await window.contentsEditorStore.write(key, 'another tab');
                await window.contentsEditorStore.write(key, text);
                const replacementWrites = writes;
                await new Promise((resolve, reject) =>
                {
                    const request = indexedDB.open('contents.editor', 1);
                    request.onerror = () => reject(request.error);
                    request.onsuccess = () =>
                    {
                        const database = request.result, transaction = database.transaction('values', 'readwrite');
                        transaction.objectStore('values').delete(key);
                        transaction.oncomplete = () =>
                        {
                            database.close();
                            resolve();
                        };
                        transaction.onabort = () => reject(transaction.error);
                    };
                });
                await window.contentsEditorStore.write(key, text);
                return { identicalWrites, replacementWrites, writes, restored: (await window.contentsEditorStore.read(key)) === text };
            }
            finally
            {
                IDBObjectStore.prototype.put = put;
            }
        });
        assert.deepEqual(result, { identicalWrites: 1, replacementWrites: 3, writes: 4, restored: true });
    });

    await t.test('full localStorage migrates all owners and clusters without changing draft bytes', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const keys = ['contents.blazor.draft.owner.["z2","peer","wf"]', 'contents.blazor.draft.old.["z0","peer","wf"]',
                'contents.editor.references.["z2","peer"]', 'contents.editor.saved.["z2","peer","wf"]',
                'contents.blazor.editor.index.owner', 'contents.blazor.force-save.backup', 'contents.draft.legacy.wf'];
            for (const key of keys)
            {
                localStorage.setItem(key, JSON.stringify({ text: 'x'.repeat(550000), pending: 'unsent', dirty: true }));
            }
            localStorage.setItem('contents.language', 'ja');
            let quota;
            try
            {
                localStorage.setItem(keys[0], 'x'.repeat(5 * 1024 * 1024));
            }
            catch (error)
            {
                quota = error.name;
            }
            const originals = keys.map(key => localStorage.getItem(key));
            const first = await window.contentsEditorStore.read(keys[0]);
            const exact = (await Promise.all(keys.map(key => window.contentsEditorStore.read(key)))).every((v, i) => v === originals[i]);
            await window.contentsEditorStore.write(keys[0], '大'.repeat(6 * 1024 * 1024));
            return { quota, exact, firstPreserved: first === originals[0], remaining: Object.keys(localStorage), key: keys[0] };
        });
        assert.equal(result.quota, 'QuotaExceededError');
        assert.equal(result.exact, true);
        assert.equal(result.firstPreserved, true);
        assert.deepEqual(result.remaining, ['contents.language']);
        await page.reload();
        assert.equal(await page.evaluate(async key => (await window.contentsEditorStore.read(key)).length, result.key), 6 * 1024 * 1024);
    });

    await t.test('migration abort preserves originals and allows reading them for export', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const key = 'contents.blazor.draft.original';
            localStorage.setItem(key, 'unsaved original');
            const put = IDBObjectStore.prototype.put;
            IDBObjectStore.prototype.put = function (...args)
            {
                const request = put.apply(this, args);
                request.addEventListener('success', () => this.transaction.abort());
                return request;
            };
            const read = await window.contentsEditorStore.read(key);
            let rejected = false;
            try
            {
                await window.contentsEditorStore.write(key, 'new');
            }
            catch
            {
                rejected = true;
            }
            const retained = localStorage.getItem(key);
            IDBObjectStore.prototype.put = put;
            const recovered = await window.contentsEditorStore.read(key);
            return { read, rejected, retained, recovered, removed: localStorage.getItem(key) === null };
        });
        assert.deepEqual(result, { read: 'unsaved original', rejected: true, retained: 'unsaved original', recovered: 'unsaved original', removed: true });
    });

    await t.test('an old UI concurrent edit is retained, then archived without overwriting the current draft', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const key = 'contents.blazor.draft.concurrent';
            localStorage.setItem(key, 'original');
            const put = IDBObjectStore.prototype.put;
            IDBObjectStore.prototype.put = function (...args)
            {
                const request = put.apply(this, args);
                request.addEventListener('success', () => localStorage.setItem(key, 'old tab edit'));
                return request;
            };
            const migrated = await window.contentsEditorStore.read(key);
            IDBObjectStore.prototype.put = put;
            const retained = localStorage.getItem(key);
            await window.contentsEditorStore.write(key, 'current UI edit');
            const current = await window.contentsEditorStore.read(key);
            const recovery = await new Promise((resolve, reject) =>
            {
                const request = indexedDB.open('contents.editor', 1);
                request.onerror = () => reject(request.error);
                request.onsuccess = () =>
                {
                    const db = request.result, tx = db.transaction('recovery'), values = tx.objectStore('recovery').getAll();
                    tx.oncomplete = () =>
                    {
                        db.close();
                        resolve(values.result);
                    };
                };
            });
            return { migrated, retained, current, recovery };
        });
        assert.equal(result.migrated, 'original');
        assert.equal(result.retained, 'old tab edit');
        assert.equal(result.current, 'current UI edit');
        assert.deepEqual(result.recovery, [{ key: 'contents.blazor.draft.concurrent', text: 'old tab edit' }]);
    });

    await t.test('a failed write is not acknowledged and the committed draft survives', async sub =>
    {
        const { page } = await fixtureAsync(sub);
        const result = await page.evaluate(async () =>
        {
            const key = 'contents.blazor.draft.aborted';
            await window.contentsEditorStore.write(key, 'committed');
            const put = IDBObjectStore.prototype.put;
            IDBObjectStore.prototype.put = function (...args)
            {
                const request = put.apply(this, args);
                request.addEventListener('success', () => this.transaction.abort());
                return request;
            };
            let rejected = false;
            try
            {
                await window.contentsEditorStore.write(key, 'uncommitted');
            }
            catch
            {
                rejected = true;
            }
            IDBObjectStore.prototype.put = put;
            return { rejected, saved: await window.contentsEditorStore.read(key) };
        });
        assert.deepEqual(result, { rejected: true, saved: 'committed' });
    });

    await t.test('reload retains the owner while a duplicated live tab receives an independent owner', async sub =>
    {
        const { context, page } = await fixtureAsync(sub);
        const owner = await page.evaluate(() => window.contentsEditorStore.owner(null, crypto.randomUUID()));
        for (let i = 0; i < 5; i++)
        {
            await page.reload();
            assert.equal(await page.evaluate(previous => window.contentsEditorStore.owner(previous, crypto.randomUUID()), owner), owner);
        }
        const other = await context.newPage();
        await other.goto(url);
        const fork = await other.evaluate(previous => window.contentsEditorStore.owner(previous, crypto.randomUUID()), owner);
        assert.notEqual(fork, owner);
        assert.equal(await page.evaluate(previous => window.contentsEditorStore.owner(previous, previous), owner).catch(() => 'occupied'), 'occupied');
        await other.reload();
        assert.equal(await other.evaluate(previous => window.contentsEditorStore.owner(previous, crypto.randomUUID()), fork), fork);
    });
});
