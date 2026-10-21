import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { ESLint } from 'eslint';

/** 実際の適用設定から検査器を構成します。 */
const eslint = new ESLint({ cwd: fileURLToPath(new URL('../../../', import.meta.url)) });
const root = 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/';

test('製品の未処理Promiseを型情報で検出する', async () =>
{
    const [result] = await eslint.lintText('Promise.resolve(1);', { filePath: root + 'remote-tools.js' });
    assert.ok(result.messages.some(message => message.ruleId === '@typescript-eslint/no-floating-promises'));
});

test('Workerではブラウザーのdocumentを使用できない', async () =>
{
    const [result] = await eslint.lintText('document.createElement("div");', { filePath: root + 'remote-desktop-worker.js' });
    assert.ok(result.messages.some(message => message.ruleId === 'no-undef'));
});

test('未定義参照はインライン指示で抑制できない', async () =>
{
    const [result] = await eslint.lintText('/* eslint-disable */\nmissing();', { filePath: root + 'remote-tools.js' });
    assert.ok(result.messages.some(message => message.ruleId === 'no-undef'));
    assert.ok(result.messages.some(message => message.ruleId === 'quality/comment-governance'));
});

test('HTMLに埋めた実行コードも通常のESLint呼出しで拒否する', async () =>
{
    const [result] = await eslint.lintText('<script>run()</script>', { filePath: root + 'index.html' });
    assert.ok(result.messages.some(message => message.ruleId === 'quality/markup-boundary'));
});

test('第三者コードと生成物は検査対象へ混入しない', async () =>
{
    assert.equal(await eslint.isPathIgnored(root + 'vendor/xterm/xterm.js'), true);
    assert.equal(await eslint.isPathIgnored('src/sample/obj/generated.js'), true);
    assert.equal(await eslint.isPathIgnored(root + 'browser.js'), false);
});

test('空行と単行コメントの自動修正が互いに打ち消し合わない', async () =>
{
    const fixing = new ESLint({ cwd: fileURLToPath(new URL('../../../', import.meta.url)), fix: true });
    const source = '/** 計測値を保持します。\n */\nexport class Counter\n{\n\n/** 値を初期化します。\n */\nreset()\n{\nthis.value = 0;\n}\n/** 現在値を取得します。\n * @returns 保持した値です。\n */\nread()\n{\nreturn this.value;\n}\n}\n';
    const [first] = await fixing.lintText(source, { filePath: 'eng/javascript/tests/layout-fixture.mjs' });
    assert.deepEqual(first.messages, []);
    assert.ok(first.output.includes('/** 計測値を保持します。 */'));
    assert.ok(first.output.includes('{\n    /** 値を初期化します。 */'));
    assert.ok(first.output.includes('}\n\n    /** 現在値を取得します。'));
    const [second] = await fixing.lintText(first.output, { filePath: 'eng/javascript/tests/layout-fixture.mjs' });
    assert.deepEqual(second.messages, []);
    assert.equal(second.output, undefined);
});
