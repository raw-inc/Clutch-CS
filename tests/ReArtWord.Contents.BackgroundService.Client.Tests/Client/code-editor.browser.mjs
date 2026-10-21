import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium } from 'playwright';

/** 実製品のモジュールを隔離ブラウザーへ配信します。 */
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
                response.end('<!doctype html><link rel="stylesheet" href="/script-editor.css"><fieldset><div class="code-input"><pre></pre><textarea aria-label="Script code"></textarea></div></fieldset><button id="next">Next</button>');
                return;
            }
            const file = path.resolve(root, '.' + decodeURIComponent(pathname));
            assert.ok(file.startsWith(root));
            response.setHeader('Content-Type', file.endsWith('.css') ? 'text/css' : 'text/javascript');
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
    origin = `http://127.0.0.1:${server.address().port}`;
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

/** 編集の変更をネイティブ入力側で観測します。
 * @returns 一件のテストが所有するブラウザーページです。
 */
async function openEditorAsync(context, language, text)
{
    const page = await browser.newPage();
    context.after(() => page.close());
    await page.goto(origin);
    await page.addStyleTag({ content: '.code-input{height:280px;width:700px;--ink:#eee;--paper:#202329;--line:#555;--muted:#aaa;--accent:#aac}' });
    await page.evaluate(async ({ language, text }) =>
    {
        const { attachCodeEditor } = await import('/script-code.js');
        const input = document.querySelector('textarea');
        input.value = text;
        window.edits = [];
        window.analyses = [];
        input.addEventListener('input', () => window.edits.push(input.value));
        input.addEventListener('change', () => window.analyses.push(input.value));
        window.editor = attachCodeEditor(input, document.querySelector('pre'), language);
    }, { language, text });
    await page.locator('.cm-content').focus();
    await page.keyboard.press('Control+End');
    return page;
}

for (const [language, code, spaces] of [
    ['csharp', 'public static int Run() {', 4], ['python', 'def run():', 4], ['javascript', 'function run() {', 4],
    ['json', '{', 2], ['yaml', 'root:', 2], ['bash', 'if true; then', 4], ['powershell', 'if ($true) {', 4], ['toml', '  key = 1', 2]
])
{
    test(`${language}の改行で適切な字下げを挿入する`, async context =>
    {
        const page = await openEditorAsync(context, language, code);
        await page.keyboard.press('Enter');
        assert.equal(await page.locator('textarea').inputValue(), code + '\n' + ' '.repeat(spaces));
        assert.equal(await page.evaluate(() => window.edits.length), 1);
    });
}

test('入力補完の選択が保存用入力へ反映され、メンバー候補も表示する', async context =>
{
    const page = await openEditorAsync(context, 'csharp', 'Con');
    await page.keyboard.press('Control+Space');
    await page.getByRole('option', { name: 'Console', exact: true }).click();
    assert.equal(await page.locator('textarea').inputValue(), 'Console');
    await page.keyboard.type('.');
    await page.getByRole('option', { name: 'WriteLine', exact: true }).waitFor();
});

test('字下げ・Undo・外部の草稿変更・読み取り専用を同じ入力へ反映する', async context =>
{
    const page = await openEditorAsync(context, 'python', 'first\nsecond');
    await page.keyboard.press('Control+A');
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('textarea').inputValue(), '    first\n    second');
    await page.keyboard.press('Control+z');
    assert.equal(await page.locator('textarea').inputValue(), 'first\nsecond');
    await page.evaluate(() =>
    {
        document.querySelector('fieldset').disabled = true;
        document.querySelector('textarea').value = 'remote update';
        window.editor.update('javascript');
    });
    await page.waitForFunction(() => document.querySelector('.cm-content').contentEditable === 'false');
    await page.keyboard.type('ignored');
    assert.equal(await page.locator('textarea').inputValue(), 'remote update');
    await page.evaluate(() => window.editor.dispose());
    assert.equal(await page.locator('.cm-editor').count(), 0);
    assert.equal(await page.locator('textarea').isVisible(), true);
});

test('IMEの未確定文字列を重複させず、日本語を確定する', async context =>
{
    const page = await openEditorAsync(context, 'csharp', '');
    const session = await page.context().newCDPSession(page);
    await session.send('Input.imeSetComposition', { text: 'にほん', selectionStart: 3, selectionEnd: 3 });
    await session.send('Input.insertText', { text: '日本' });
    await page.waitForFunction(() => document.querySelector('textarea').value === '日本');
    assert.equal(await page.locator('textarea').inputValue(), '日本');
});

test('画面から切り離した後の破棄と再度の破棄も安全に完了する', async context =>
{
    const page = await openEditorAsync(context, 'javascript', 'const value = 1;');
    await page.evaluate(() =>
    {
        document.querySelector('.code-input').remove();
        window.editor.dispose();
        window.editor.dispose();
        window.editor.update('python');
    });
    assert.equal(await page.locator('.cm-editor').count(), 0);
});

test('Escapeに続くTabでコード欄から次の操作へ移動できる', async context =>
{
    const page = await openEditorAsync(context, 'javascript', 'const value = 1;');
    await page.keyboard.press('Escape');
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('#next').evaluate(button => button === document.activeElement), true);
    assert.equal(await page.locator('textarea').inputValue(), 'const value = 1;');
});

for (const [language, prefix, label] of [['csharp', 'pub', 'public'], ['python', 'de', 'def'], ['javascript', 'fun', 'function']])
{
    test(`${language}の宣言語はTabで名前と区切りだけを補い、引数や本体を作らない`, async context =>
    {
        const page = await openEditorAsync(context, language, prefix);
        await page.keyboard.press('Control+Space');
        await page.getByRole('option', { name: label, exact: true }).waitFor();
        await page.keyboard.press('Tab');
        assert.equal(await page.locator('textarea').inputValue(), label + ' ');
        await page.keyboard.type('Example');
        assert.equal(await page.locator('textarea').inputValue(), label + ' Example');
    });
}

for (const [language, declaration] of [['csharp', 'public static int Calculate'], ['python', 'def calculate'], ['javascript', 'export async function calculate']])
{
    test(`${language}のメソッド宣言のTabは空の括弧内へ移動する`, async context =>
    {
        const page = await openEditorAsync(context, language, declaration);
        await page.keyboard.press('Tab');
        assert.equal(await page.locator('textarea').inputValue(), declaration + '()');
        await page.keyboard.type('value');
        assert.equal(await page.locator('textarea').inputValue(), declaration + '(value)');
    });
}

test('メソッド名の補完は括弧内へカーソルを移し、引数を自動生成しない', async context =>
{
    const page = await openEditorAsync(context, 'csharp', 'Console.WriteLi');
    await page.keyboard.press('Control+Space');
    await page.getByRole('option', { name: 'WriteLine', exact: true }).waitFor();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('textarea').inputValue(), 'Console.WriteLine()');
    await page.keyboard.type('"hello"');
    assert.equal(await page.locator('textarea').inputValue(), 'Console.WriteLine("hello")');
});

test('既存の括弧を補完で重複させず、Undoは確定前の入力へ戻す', async context =>
{
    const page = await openEditorAsync(context, 'csharp', 'Console.WriteLi()');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('Control+Space');
    await page.getByRole('option', { name: 'WriteLine', exact: true }).waitFor();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('textarea').inputValue(), 'Console.WriteLine()');
    await page.keyboard.press('Control+z');
    assert.equal(await page.locator('textarea').inputValue(), 'Console.WriteLi()');
});

test('通常の変数宣言でのTabは字下げを行い、波括弧は閉じ側だけを補う', async context =>
{
    const page = await openEditorAsync(context, 'csharp', 'int value');
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('textarea').inputValue(), '    int value');
    await page.keyboard.press('Control+A');
    await page.keyboard.press('Backspace');
    await page.keyboard.type('{');
    assert.equal(await page.locator('textarea').inputValue(), '{}');
    await page.keyboard.type('value');
    assert.equal(await page.locator('textarea').inputValue(), '{value}');
});

test('Pythonの標準メンバーでも、モジュールへ呼出し括弧を補わない', async context =>
{
    const page = await openEditorAsync(context, 'python', 'os.pa');
    await page.keyboard.press('Control+Space');
    await page.getByRole('option', { name: 'path', exact: true }).waitFor();
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('textarea').inputValue(), 'os.path');
});

test('フォーカスを維持したまま連続入力をまとめ、最後のコードだけを自動解析へ通知する', async context =>
{
    const page = await openEditorAsync(context, 'csharp', '');
    await page.clock.install();
    await page.keyboard.insertText('public static int Run()');
    await page.clock.fastForward(400);
    assert.deepEqual(await page.evaluate(() => window.analyses), []);
    await page.keyboard.insertText(' => 1;');
    await page.clock.fastForward(500);
    assert.deepEqual(await page.evaluate(() => window.analyses), []);
    await page.clock.fastForward(150);
    assert.deepEqual(await page.evaluate(() => window.analyses), ['public static int Run() => 1;']);
    assert.equal(await page.locator('.cm-content').evaluate(content => content === document.activeElement), true);
});

for (const operation of ['blur', 'lock', 'replace', 'dispose'])
{
    test(`${operation}で古いコードの自動解析予約を取り消す`, async context =>
    {
        const page = await openEditorAsync(context, 'javascript', '');
        await page.clock.install();
        await page.keyboard.insertText('export function run() {return 1;}');
        await page.evaluate(operation =>
        {
            switch (operation)
            {
                case 'blur': document.querySelector('#next').focus();
                    break;
                case 'lock': document.querySelector('fieldset').disabled = true;
                    break;
                case 'replace':
                    document.querySelector('textarea').value = 'export function run() {return "new";}';
                    window.editor.update('javascript');
                    break;
                case 'dispose': window.editor.dispose();
                    break;
            }
        }, operation);
        await page.clock.fastForward(1000);
        assert.deepEqual(await page.evaluate(() => window.analyses), []);
    });
}

test('IME変換中には解析せず、確定した文字列を解析へ渡す', async context =>
{
    const page = await openEditorAsync(context, 'python', '');
    await page.clock.install();
    const session = await page.context().newCDPSession(page);
    await session.send('Input.imeSetComposition', { text: 'にほん', selectionStart: 3, selectionEnd: 3 });
    await page.clock.fastForward(1000);
    assert.deepEqual(await page.evaluate(() => window.analyses), []);
    await session.send('Input.insertText', { text: '日本' });
    await page.clock.fastForward(1000);
    assert.deepEqual(await page.evaluate(() => window.analyses), ['日本']);
});
