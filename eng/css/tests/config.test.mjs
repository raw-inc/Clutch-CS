import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import stylelint from 'stylelint';
import { inspectMarkup } from '../../javascript/markup.mjs';

/** 実際の構成を使い、ソース断片の診断と修正結果を取得します。
 * @returns 検査結果です。
 */
async function inspectAsync(code, filename = 'src/Example.css', fix = false)
{
    return stylelint.lint({ code, codeFilename: fileURLToPath(new URL('../../../' + filename, import.meta.url)),
        configFile: fileURLToPath(new URL('../../../stylelint.config.mjs', import.meta.url)), fix });
}

/** 指定した診断が実際の設定で有効かを確認します。
 * @returns 検査完了です。
 */
async function rejectsAsync(code, rule, filename)
{
    const result = await inspectAsync(code, filename);
    assert.ok(result.results[0].warnings.some(warning => warning.rule === rule), JSON.stringify(result.results[0].warnings));
}

test('未知のプロパティ・値・単位を拒否する', async () =>
{
    await rejectsAsync('.sample { colr: red; }', 'property-no-unknown');
    await rejectsAsync('.sample { display: banana; }', 'declaration-property-value-no-unknown');
    await rejectsAsync('.sample { width: 12pxx; }', 'declaration-property-value-no-unknown');
});

test('壊れた構文と不正な色を拒否する', async () =>
{
    await rejectsAsync('.sample { color: red;', 'CssSyntaxError');
    await rejectsAsync('.sample { color: #12345; }', 'declaration-property-value-no-unknown');
});

test('重複する宣言・セレクター・カスタムプロパティを拒否する', async () =>
{
    await rejectsAsync('.sample { color: red; color: red; }', 'declaration-block-no-duplicate-properties');
    await rejectsAsync('.sample { color: red; } .sample { padding: 0; }', 'no-duplicate-selectors');
    await rejectsAsync('.sample { --accent: red; --accent: blue; }', 'declaration-block-no-duplicate-custom-properties');
});

test('ID依存と過度な詳細度、命名違反を拒否する', async () =>
{
    await rejectsAsync('#target { color: red; }', 'selector-max-id');
    await rejectsAsync('.a.b.c.d.e.f.g { color: red; }', 'selector-max-specificity');
    await rejectsAsync('.myControl { color: red; }', 'selector-class-pattern');
});

test('理由のないimportantを拒否し、宣言直前の日本語の理由だけを受け付ける', async () =>
{
    await rejectsAsync('[hidden] { display: none !important; }', 'quality/important-reason');
    await rejectsAsync('/* 例外理由: 日本語の理由です。 */ [hidden] { display: none !important; }', 'quality/important-reason');
    const result = await inspectAsync('[hidden] { /* 例外理由: hidden 属性の非表示を保証します。 */ display: none !important; }', undefined, true);
    assert.ok(!result.results[0].warnings.some(warning => warning.rule === 'quality/important-reason'));
});

test('無効化コメントで検査を回避できない', async () =>
{
    const result = await inspectAsync('/* stylelint-disable */\n.sample { colr: red; }');
    assert.equal(result.errored, true);
    assert.ok(result.results[0].warnings.some(warning => warning.rule === 'quality/comments'));
    assert.ok(result.results[0].warnings.some(warning => warning.rule === 'property-no-unknown'));
    const described = await inspectAsync('/* stylelint-disable -- 例外の説明があります。 */\n.sample { colr: red; }');
    assert.ok(described.results[0].warnings.some(warning => warning.rule === 'property-no-unknown'));
});

test('一行コメントの閉じ記号と保留の解消条件を検査する', async () =>
{
    await rejectsAsync('/* 一行の説明です。\n */\n.sample { color: red; }', 'quality/comments');
    await rejectsAsync('/* TODO: 色を見直します。 */\n.sample { color: red; }', 'quality/comments');
    const result = await inspectAsync('/* TODO: 旧テーマとの互換用です。解消条件: 旧テーマ廃止時に削除します。 */\n.sample { color: red; }', undefined, true);
    assert.ok(!result.results[0].warnings.some(warning => warning.rule === 'quality/comments'));
});

test('deepはRazor CSSだけで認め、未知の疑似要素やグローバル定義は拒否する', async () =>
{
    const source = '.panel ::deep .child { color: red; }';
    await rejectsAsync(source, 'selector-pseudo-element-no-unknown');
    const isolated = await inspectAsync(source, 'src/Example.razor.css', true);
    assert.equal(isolated.errored, false);
    await rejectsAsync('.panel::unknown { color: red; }', 'selector-pseudo-element-no-unknown', 'src/Example.razor.css');
    await rejectsAsync('body { color: red; }', 'selector-disallowed-list', 'src/Example.razor.css');
});

test('第三者CSSと生成物だけを検査から除く', async () =>
{
    for (const path of ['src/Example/vendor/library.css', 'src/Example/obj/generated.css', 'src/Example/bin/generated.css'])
    {
        const result = await inspectAsync('.x { wrong: bad; }', path);
        assert.equal(result.results[0].ignored, true);
    }
    await rejectsAsync('.x { wrong: bad; }', 'property-no-unknown', 'src/Example/wwwroot/component.css');
});

test('自動修正は宣言値と順番を保持し、二回目に追加の修正を要求しない', async () =>
{
    const first = await inspectAsync('.sample{display:flex;gap:8px;color:var(--ink)}@media(max-width:750px){.sample{display:block}}', undefined, true);
    assert.equal(first.errored, false);
    assert.ok(first.code.replaceAll('\r\n', '\n').includes('    display: flex;\n    gap: 8px;\n    color: var(--ink);'));
    const second = await inspectAsync(first.code, undefined, true);
    assert.equal(second.errored, false);
    assert.equal(second.code, first.code);
});

test('画面内のCSS定義と固定style属性を拒否し、動的な寸法は許可する', () =>
{
    assert.ok(inspectMarkup('<style>.x { color: red }</style>', true).some(message => message.ruleId === 'quality/style-boundary'));
    assert.ok(inspectMarkup('<span style="display:flex">label</span>', true).some(message => message.ruleId === 'quality/style-boundary'));
    assert.deepEqual(inspectMarkup('<div style="@Position"></div>', true), []);
    assert.deepEqual(inspectMarkup('<div style="width:@Width"></div>', true), []);
    assert.ok(inspectMarkup('<div style="@Position"></div>').some(message => message.ruleId === 'quality/style-boundary'));
});
