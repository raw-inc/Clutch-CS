import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';
import { loadStylesAsync } from './css-browser.mjs';

test('同梱モジュールの色分けは選択・スクロール・元テキストを保ち、コードを実行しない', async () =>
{
    const client = fileURLToPath(new URL('../../../src/ReArtWord.Contents.BackgroundService.Client/', import.meta.url));
    const webRoot = path.resolve(client, 'wwwroot');
    const styles = await loadStylesAsync(webRoot, path.join(client, 'obj', process.env.CONTENTS_TEST_CONFIGURATION ?? 'Debug', 'net10.0/scopedcss/bundle/ReArtWord.Contents.BackgroundService.Client.styles.css'));
    const server = createServer(async (request, response) =>
    {
        try
        {
            const url = new URL(request.url, 'http://localhost');
            if (url.pathname === '/')
            {
                response.writeHead(200, { 'Content-Type': 'text/html' });
                response.end('<!doctype html><div class="code-input"><pre></pre><textarea></textarea></div>');
                return;
            }
            const file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
            if (!file.startsWith(webRoot + path.sep) || !file.endsWith('.js'))
            {
                response.writeHead(404).end();
                return;
            }
            response.writeHead(200, { 'Content-Type': 'text/javascript' });
            response.end(await readFile(file));
        }
        catch
        {
            response.writeHead(404).end();
        }
    });
    await new Promise(resolve =>
    {
        server.listen(0, '127.0.0.1', resolve);
    });
    let browser;
    try
    {
        browser = await chromium.launch({ headless: true });
        const page = await browser.newPage();
        const origin = `http://127.0.0.1:${server.address().port}`;
        const requests = [];
        page.on('request', request => requests.push(request.url()));
        await page.goto(origin);
        await page.addStyleTag({ content: styles.css });
        await page.evaluate(scope =>
        {
            for (const element of document.querySelectorAll('.code-input, pre, textarea'))
            {
                element.setAttribute(scope, '');
            }
        }, styles.scopes['WorkflowEditor/Fields/ScriptCodeField']);
        await page.evaluate(async () =>
        {
            const module = await import('/script-code.js');
            globalThis.codeEditor = module.attachCodeEditor(document.querySelector('textarea'), document.querySelector('pre'), 'text');
        });
        for (const [language, source, token] of [
            ['csharp', 'public static int Run() => 42;', '.code-keyword'],
            ['python', 'def run(): return 42', '.code-keyword'],
            ['javascript', 'const value = 42;', '.code-keyword'],
            ['bash', 'echo "$HOME"', '.code-variable'],
            ['powershell', '$value = 42', '.code-variable'],
            ['json', '{"value":42}', '.code-property'],
            ['toml', 'value = 42', '.code-property'],
            ['yaml', 'value: 42', '.code-property']
        ])
        {
            const code = source + '\n'.repeat(60) + '<img src=x onerror="globalThis.executed=true">';
            await page.evaluate(({ code, language }) =>
            {
                const input = document.querySelector('textarea');
                input.value = code;
                globalThis.codeEditor.update(language);
            }, { code, language });
            await page.locator('.cm-content').focus();
            await page.keyboard.press('Control+Home');
            const colors = await page.locator('.cm-content').evaluate((content, token) => ({
                color: getComputedStyle(content.querySelector(token)).color, plainColor: getComputedStyle(content).color
            }), token);
            assert.notEqual(colors.color, colors.plainColor);
            await page.keyboard.press('Control+End');
            await page.keyboard.press('Shift+Home');
            await page.waitForFunction(() => 0 < document.querySelector('.cm-scroller').scrollTop);
            const state = await page.evaluate(language =>
            {
                const scroll = document.querySelector('.cm-scroller'), before = scroll.scrollTop;
                const selection = globalThis.getSelection().toString();
                globalThis.codeEditor.update(language);
                return { value: document.querySelector('textarea').value, selection, afterSelection: globalThis.getSelection().toString(),
                    before, after: scroll.scrollTop, images: document.querySelector('.cm-content').querySelectorAll('img').length, executed: globalThis.executed === true };
            }, language);
            assert.equal(state.value, code);
            assert.equal(state.selection, '<img src=x onerror="globalThis.executed=true">');
            assert.equal(state.afterSelection, state.selection);
            assert.equal(state.after, state.before);
            assert.equal(state.images, 0);
            assert.equal(state.executed, false);
        }
        await page.evaluate(() => globalThis.codeEditor.dispose());
        assert.ok(requests.every(url => url.startsWith(origin + '/')));
        assert.ok(requests.some(url => url.includes('/vendor/code-editor/codemirror.js')));
    }
    finally
    {
        await browser?.close();
        await new Promise((resolve, reject) =>
        {
            server.close(error => error ? reject(error) : resolve());
        });
    }
});
