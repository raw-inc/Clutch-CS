// API・秘密・管理操作の応答はキャッシュしない。公開時に検証済みの画面資材だけを保持する。
const requestedVersion = new URL(self.location.href).searchParams.get('ui');
self.importScripts('./service-worker-assets.js' + (requestedVersion ? '?ui=' + encodeURIComponent(requestedVersion) : ''));
const prefix = 'contents-ui-', cacheName = prefix + self.assetsManifest.version;
const resources = self.assetsManifest.assets.filter(asset => /\.(wasm|dll|js|json|webmanifest|css|html|svg|png|dat|pdb)$/.test(asset.url) && !asset.url.includes('service-worker') && asset.url !== 'ui-update.html');
const entryPaths = ['/', '/ui-start.html', '/ui-update.js', '/ui-update.css'];
let completed = 0, lastProgress = 0;

/** 資材の取得・検証の進捗を画面へ通知します。
 * @returns 処理の完了と結果を伝えるPromiseです。
 */
async function reportProgressAsync()
{
    const now = Date.now();
    if (completed !== resources.length && now - lastProgress < 80)
    {
        return;
    }
    lastProgress = now;
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows)
    {
        if (['/', '/ui-start.html', '/ui-update.html'].includes(new URL(client.url).pathname))
        {
            try
            {
                client.postMessage({ type: 'contents.pwa.progress', completed, total: resources.length, version: self.assetsManifest.version });
            }
            catch
            {/* 表示ページの終了で進捗通知が届かなくても、資材の検証と保存は継続します。 */}
        }
    }
}

self.addEventListener('install', event => event.waitUntil((async() =>
{
    const cache = await caches.open(cacheName);
    let next = 0;

    // Immutable query identities also separate old CDN objects; integrity remains mandatory.
    await Promise.all(Array.from({ length: Math.min(6, resources.length) }, async() =>
    {
        while (next < resources.length)
        {
            const asset = resources[next++], url = new URL(asset.url, self.registration.scope);
            url.searchParams.set('__ui', self.assetsManifest.version);
            const response = await fetch(new Request(url, { integrity: asset.hash, cache: 'no-store' }), { signal: AbortSignal.timeout(120000) });
            if (!response.ok)
            {
                throw new Error('UI asset download failed.');
            }
            await cache.put(asset.url, response);
            completed++;
            await reportProgressAsync();
        }
    }));
})()));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith(prefix) && key !== cacheName).map(key => caches.delete(key))))));
self.addEventListener('fetch', event =>
{
    const expectedGet = 'GET';
    const expectedHealth = '/health';
    const expectedUiUpdateHtml = '/ui-update.html';

    const url = new URL(event.request.url);
    if (event.request.method !== expectedGet || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname.startsWith('/client/') || url.pathname === expectedHealth || url.pathname === expectedUiUpdateHtml)
    {
        return;
    }
    if (entryPaths.includes(url.pathname))
    {
        // Always check the entry page online; retain a working offline route to the cached Dashboard.
        event.respondWith(fetch(event.request, { signal: event.request.signal }).catch(async error =>
        {
            const expectedValue = '/';

            const cached = await (await caches.open(cacheName)).match(url.pathname === expectedValue ? 'ui-start.html' : url.pathname.slice(1));
            if (cached)
            {
                return cached;
            }
            throw error;
        }));
        return;
    }
    const navigation = event.request.mode === 'navigate' && /^\/(dashboard|editor|host-manager|file-transfer|browser-transfer|terminal|desktop|signal-scope|logs|licenses)\/?$/.test(url.pathname);
    let key = event.request;
    if (navigation)
    {
        key = 'index.html';
    }
    else if (url.searchParams.size === 1 && url.searchParams.get('__ui') === self.assetsManifest.version)
    {
        // 検証時の応答URLを引き継ぐ動的importも、同じ版に限って正規キーから取得します。
        key = url.pathname.slice(1);
    }
    event.respondWith(caches.open(cacheName).then(async cache => (await cache.match(key)) ?? fetch(event.request, { signal: event.request.signal })));
});

self.addEventListener('message', event =>
{
    const expectedContentsPwaProgress = 'contents.pwa.progress';
    const expectedContentsPwaActivate = 'contents.pwa.activate';
    const expectedContentsPwaStatus = 'contents.pwa.status';

    if (event.data?.type === expectedContentsPwaProgress && event.ports[0])
    {
        event.waitUntil(caches.open(cacheName).then(async cache => event.ports[0].postMessage({ completed: (await cache.keys()).length, total: resources.length, version: self.assetsManifest.version })));
        return;
    }
    if (event.data?.type === expectedContentsPwaActivate && event.ports[0])
    {
        event.waitUntil((async() =>
        {
            const source = event.source, windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
            if (!source || !['/', '/ui-start.html', '/ui-update.html'].includes(new URL(source.url).pathname) || windows.some(client => client.id !== source.id && client.url.startsWith(self.registration.scope)))
            {
                event.ports[0].postMessage({ accepted: false });
                return;
            }
            event.ports[0].postMessage({ accepted: true });
            await self.skipWaiting();
        })());
        return;
    }
    if (event.data?.type !== expectedContentsPwaStatus || !event.ports[0])
    {
        return;
    }
    event.waitUntil(caches.open(cacheName).then(async cache =>
    {
        const present = await cache.keys();
        event.ports[0].postMessage({ published: true, ready: present.length === resources.length, version: self.assetsManifest.version });
    }));
});
