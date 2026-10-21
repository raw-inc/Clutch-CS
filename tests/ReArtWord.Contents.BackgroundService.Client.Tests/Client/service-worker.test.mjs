import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';
const source = await fs.readFile(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/service-worker.published.js', import.meta.url), 'utf8');

/** 対象機能のブラウザー境界と操作可能な試験状態を構成します。
 * @returns 呼出し元が使用する値です。
 */
function fixture()
{
    const events = {}, requests = [], stored = [], replies = [], imports = [], progress = [];
    let skipped = 0, windows = [{ id: 'update', url: 'https://app.example/ui-update.html',

        postMessage: value => progress.push(value) }];
    const self = { location: new URL('https://app.example/service-worker.js?ui=revision'), registration: { scope: 'https://app.example/' },
        assetsManifest: { version: 'new-version', assets: [{ url: 'index.html', hash: 'sha256-test' }, { url: 'browser.js', hash: 'sha256-test' },
            { url: 'favicon.svg', hash: 'sha256-test' }, { url: 'manifest.webmanifest', hash: 'sha256-test' },
            { url: 'icons/app-192.png', hash: 'sha256-test' }, { url: 'icons/app-512.png', hash: 'sha256-test' }, { url: 'ui-update.html', hash: 'sha256-test' }] },

        importScripts: path => imports.push(path),

        addEventListener: (name, fn) =>
        {
            const assignedValue = events[name] = fn;
            return assignedValue;
        },

        clients: { matchAll: async() => windows },

        skipWaiting: async() =>
        {
            skipped++;
        } };
    const cache = { put: async(key, response) => stored.push({ key, response }),

        keys: async() => stored.map(item => item.key) };
    const context = { self, URL, Request, AbortSignal, caches: { open: async() => cache,

        keys: async() => [],

        delete: async() => true },

    fetch: async(request) =>
    {
        requests.push(request);
        return { ok: true };
    } };
    vm.runInNewContext(source, context);

    /** 指定したイベントを登録済みのハンドラーへ渡します。
     * @returns 処理の完了と結果を伝えるPromiseです。
     */
    const dispatchAsync = async(type, data = {}) =>
    {
        let pending;
        events[type]({ ...data,

            /** Service Workerが所有する試験用Promiseを保持します。 */
            waitUntil(value)
            {
                pending = value;
            } });
        await pending;
    };

    return { requests, stored, imports, events, dispatch: dispatchAsync, replies, progress,

        get skipped()
        {
            return skipped;
        },

        /** 試験で観測するブラウザー画面の一覧を差し替えます。 */
        setWindows(value)
        {
            windows = value;
        },

        context,

        activate: () => dispatchAsync('message', { data: { type: 'contents.pwa.activate' }, source: { id: 'update', url: 'https://app.example/ui-update.html' }, ports: [{ postMessage: value => replies.push(value) }] }) };
}

test('UI installation fetches versioned URLs with integrity and stores canonical cache keys', async() =>
{
    const f = fixture();
    await f.dispatch('install');
    assert.deepEqual(f.imports, ['./service-worker-assets.js?ui=revision']);
    assert.equal(f.requests.length, 6);
    for (const request of f.requests)
    {
        assert.equal(new URL(request.url).searchParams.get('__ui'), 'new-version');
        assert.equal(request.integrity, 'sha256-test');
        assert.equal(request.cache, 'no-store');
    }
    assert.deepEqual(f.stored.map(item => item.key), ['index.html', 'browser.js', 'favicon.svg', 'manifest.webmanifest', 'icons/app-192.png', 'icons/app-512.png']);
    assert.equal(f.progress.at(-1).completed, 6);
    assert.equal(f.progress.at(-1).total, 6);
});

test('every app route reopens the verified shell offline without caching live responses', async () =>
{
    const f = fixture(), shell = { page: 'verified-ui' }, requests = [];
    f.context.caches.open = async () => ({ match: async key =>
    {
        requests.push(key);
        return key === 'index.html' ? shell : undefined;
    } });
    f.context.fetch = async () =>
    {
        throw new Error('offline');
    };
    for (const path of ['dashboard', 'editor', 'host-manager', 'file-transfer', 'browser-transfer', 'terminal', 'desktop', 'signal-scope', 'logs', 'licenses'])
    {
        let response;
        f.events.fetch({ request: { method: 'GET', url: `https://app.example/${path}/?experimental=true`, mode: 'navigate' },

            respondWith: value =>
            {
                response = value;
            } });
        assert.equal(await response, shell);
        assert.equal(requests.at(-1), 'index.html');
    }
});
test('dynamic imports use canonical cached assets only for the verified current version', async () =>
{
    const f = fixture(), cached = { asset: 'verified' }, keys = [], asset = '_framework/dotnet.runtime.js';
    f.context.caches.open = async () => ({ match: async key =>
    {
        keys.push(key);
        return key === asset ? cached : undefined;
    } });
    f.context.fetch = async () =>
    {
        throw new Error('requires network');
    };
    for (const query of ['?__ui=new-version', '?__ui=older-version', '?__ui=new-version&extra=value'])
    {
        let response;
        const request = new Request('https://app.example/' + asset + query);
        f.events.fetch({ request,

            respondWith: value =>
            {
                response = value;
            } });
        if (query === '?__ui=new-version')
        {
            assert.equal(await response, cached);
            assert.equal(keys.at(-1), asset);
        }
        else
        {
            await assert.rejects(response, /requires network/);
            assert.equal(keys.at(-1), request);
        }
    }
});

test('UI activation refuses other open app pages and never activates implicitly', async() =>
{
    const f = fixture();
    await f.dispatch('install');
    assert.equal(f.skipped, 0);
    f.setWindows([{ id: 'update', url: 'https://app.example/ui-update.html' }, { id: 'draft', url: 'https://app.example/editor' }]);
    await f.activate();
    assert.equal(f.replies.at(-1).accepted, false);
    assert.equal(f.skipped, 0);
    f.setWindows([{ id: 'update', url: 'https://app.example/ui-update.html' }]);
    await f.activate();
    assert.equal(f.replies.at(-1).accepted, true);
    assert.equal(f.skipped, 1);
});
test('management and recovery navigation never read the offline UI cache', () =>
{
    const f = fixture();
    for (const path of ['/client/dashboard/batch', '/client/branding/favicon?v=image-version', '/api/status', '/health', '/ui-update.html'])
    {
        let intercepted = false;
        f.events.fetch({ request: { method: 'GET', url: 'https://app.example' + path, mode: 'navigate' },

            /** 試験用のfetch応答を保持します。 */
            respondWith()
            {
                intercepted = true;
            } });
        assert.equal(intercepted, false);
    }
});
test('failed resource verification rejects installation instead of activating a partial UI', async() =>
{
    const f = fixture();
    f.context.fetch = async() =>
    {
        throw new Error('integrity mismatch');
    };
    await assert.rejects(f.dispatch('install'), /integrity mismatch/);
    assert.equal(f.skipped, 0);
});

test('resource cancellation rejects installation without storing or activating a partial UI', async () =>
{
    const f = fixture(), controller = new AbortController();
    const timeout = new Error('installation deadline');
    f.context.AbortSignal = { timeout: milliseconds =>
    {
        assert.equal(milliseconds, 120000);
        return controller.signal;
    } };
    f.context.fetch = (request, { signal }) => new Promise((resolve, reject) =>
    {
        assert.equal(signal, controller.signal);
        signal.addEventListener('abort', () => reject(signal.reason), { once: true });
        queueMicrotask(() => controller.abort(timeout));
    });
    await assert.rejects(f.dispatch('install'), error => error === timeout);
    assert.equal(f.stored.length, 0);
    assert.equal(f.skipped, 0);
});

test('uncached resources inherit cancellation from the intercepted browser request', async () =>
{
    const f = fixture(), controller = new AbortController();
    const request = new Request('https://app.example/browser-startup.js', { signal: controller.signal });
    let response;
    f.context.caches.open = async () => ({ match: async () => undefined });
    f.context.fetch = (incoming, { signal }) =>
    {
        assert.equal(incoming, request);
        assert.equal(signal, request.signal);
        return new Promise((resolve, reject) =>
        {
            signal.addEventListener('abort', () => reject(signal.reason), { once: true });
            queueMicrotask(() => controller.abort(new Error('navigation cancelled')));
        });
    };
    f.events.fetch({ request,

        respondWith: value =>
        {
            response = value;
        } });
    await assert.rejects(response, /navigation cancelled/);
});
