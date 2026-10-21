import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';

/** 製品の画面と通知本文だけをローカルの試験用サーバーで配信します。 */
const root = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/', import.meta.url));
let server, browser, origin;
before(async () =>
{
    server = createServer(async (request, response) =>
    {
        try
        {
            const pathname = new URL(request.url, 'http://localhost').pathname;
            if (pathname === '/')
            {
                response.setHeader('Content-Type', 'text/html; charset=utf-8');
                response.end('<!doctype html><html lang="ja"><meta charset="utf-8"><title>Licenses</title><main></main></html>');
                return;
            }
            const file = path.resolve(root, '.' + decodeURIComponent(pathname));
            assert.ok(file.startsWith(root));
            const type = file.endsWith('.js') ? 'text/javascript' : 'text/plain';
            response.setHeader('Content-Type', type + '; charset=utf-8');
            response.end(await readFile(file));
        }
        catch
        {
            response.statusCode = 404;
            response.end();
        }
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    origin = 'http://127.0.0.1:' + server.address().port;
    browser = await chromium.launch({ headless: true });
});
after(async () =>
{
    await browser?.close();
    await new Promise(resolve =>
    {
        server?.close(resolve);
    });
});

for (const language of ['ja', 'en'])
{
    test(language + ': ライブラリを名前順に表示し、採用したライセンスと本文を確認できる', async context =>
    {
        const page = await browser.newPage();
        context.after(() => page.close());
        await page.goto(origin);
        await page.evaluate(async language =>
        {
            const { licensesView } = await import('/oss-licenses.js');
            const controller = new AbortController();
            await licensesView(document.querySelector('main'), {
                t: (en, ja) => language === 'ja' ? ja : en,

                signal: controller.signal,

                api: async url =>
                {
                    const response = await fetch(url);
                    return (await response.json()).reverse();
                }
            });
        }, language);
        const names = await page.locator('section h2').allTextContents();
        assert.ok((0) < (names.length));
        assert.deepEqual(names, [...names].sort((left, right) => left.localeCompare(right, 'en', { sensitivity: 'base' })));
        const csv = page.locator('section').filter({ has: page.getByRole('heading', { name: 'CsvHelper', exact: true }) });
        const selection = language === 'ja' ? '採用ライセンス: Apache-2.0' : 'Selected license: Apache-2.0';
        assert.ok(await csv.getByText(selection, { exact: true }).isVisible());
        assert.match(await csv.locator('.muted').textContent(), /MS-PL OR Apache-2\.0/u);
        await csv.locator('summary').first().click();
        await csv.locator('pre').filter({ hasText: 'Apache License' }).waitFor({ state: 'visible' });
        const zstd = page.locator('section').filter({ has: page.getByRole('heading', { name: 'ZstdSharp.Port', exact: true }) });
        assert.match(await zstd.textContent(), /Zstandard: .*BSD-3-Clause \(BSD-3-Clause OR GPL-2\.0-only\)/u);
        const editor = page.locator('section').filter({ has: page.getByRole('heading', { name: '@codemirror/autocomplete', exact: true }) });
        assert.equal(await editor.locator('a').count(), 0);
    });
}

test('一覧から参照するすべての通知本文を取得できる', async () =>
{
    const entries = await (await fetch(origin + '/licenses/notices.json')).json();
    const paths = new Set(entries.flatMap(entry => entry.notices.map(notice => notice.href)));
    assert.ok((0) < (paths.size));
    for (const href of paths)
    {
        const response = await fetch(origin + href);
        assert.equal(response.status, 200, href);
        const text = await response.text();
        assert.ok(0 < text.trim().length, href);
        assert.ok(!text.includes('\u0000'), href);
        assert.doesNotMatch(text, /^\{\\rtf/u, href);
    }
});
