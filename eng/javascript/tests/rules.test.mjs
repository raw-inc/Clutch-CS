import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { RuleTester } from 'eslint';
import ts from 'typescript-eslint';
import quality from '../index.mjs';
import { inspectMarkup } from '../markup.mjs';

RuleTester.describe = describe;
RuleTester.it = it;

/** 規則単体の検出・非検出と、自動修正の意味を確認する実行器です。 */
const tester = new RuleTester({ languageOptions: { ecmaVersion: 'latest', sourceType: 'module' } });

tester.run('documentation', quality.rules.documentation, {
    valid: [
        '/** 値を取得します。 @returns 保存値です。 */ export function read(){return 1;}',
        '/** 呼出しを登録します。 */ function register(){ queueMicrotask(() => 1); }',
        'values.map(value => value + 1);',
        '/** 終了を待ちます。 @returns 終了確認です。 */ async function stopAsync(){}',
        { code: '/** 値を返します。 @returns 数値です。 */ function read(): number { return 1; }', languageOptions: { parser: ts.parser } }
    ],
    invalid: [
        { code: 'function read(){return 1;}', errors: [{ messageId: 'summary' }] },
        { code: '/** Reads a value. @returns 保存値です。 */ function read(){return 1;}', errors: [{ messageId: 'summary' }] },
        { code: '/** 値を読みます。 */ function read(){return 1;}', errors: [{ messageId: 'returns' }] },
        { code: '/** 値を読みます。 */ const read = () => 1;', errors: [{ messageId: 'returns' }] },
        { code: '/** 終了します。 */ async function closeAsync(){}', errors: [{ messageId: 'returns' }] },
        { code: 'class Sample {read(){return 1;}}', errors: [{ messageId: 'summary' }, { messageId: 'summary' }] },
        { code: '/** 以前の説明です。 */ const other=1; function read(){}', errors: [{ messageId: 'summary' }] }
    ]
});

tester.run('control-flow', quality.rules['control-flow'], {
    valid: ['if (a < b) {}', 'for(let i=0;i<count;i++){}', 'if(a){} else if(b){} else if(c){}'],
    invalid: [
        { code: 'if (value > 0) {}', output: 'if ((0) < (value)) {}', errors: [{ messageId: 'direction' }] },
        { code: 'if (left() >= right()) {}', output: null, errors: [{ messageId: 'direction' }] },
        { code: 'const result = a > b;', output: null, errors: [{ messageId: 'direction' }] },
        { code: 'for(;;){}', errors: [{ messageId: 'loop' }] },
        { code: 'if(a){} else if(b){} else if(c){} else if(d){}', errors: [{ messageId: 'chain' }] },
        { code: '(a&&b&&c&&d&&e)?yes:no', errors: [{ messageId: 'ternary' }] }
    ]
});

tester.run('comment-governance', quality.rules['comment-governance'], {
    valid: ['try { run(); } catch { /* 切断済みの資源は解放済みとして扱います。 */ }', '// TODO: 接続先API対応後に期限の設定を追加する。\nrun();', 'const text = "eslint-disable";'],
    invalid: [
        { code: 'try { run(); } catch {}', errors: [{ messageId: 'catch' }] },
        { code: 'try { run(); } catch { /* ignored */ }', errors: [{ messageId: 'catch' }] },
        { code: '// FIXME: later\nrun();', errors: [{ messageId: 'todo' }] },
        { code: '/* eslint no-undef: off */ run();', options: [], errors: [{ messageId: 'suppression' }] }
    ]
});

tester.run('class-structure', quality.rules['class-structure'], {
    valid: ['export class Reader {}', 'class Outer { create(){ return class Inner {}; } }'],
    invalid: [
        { code: 'class reader {}', errors: [{ messageId: 'name' }] },
        { code: 'const reader = class {};', errors: [{ messageId: 'name' }] },
        { code: 'class One {} export class Two {}', errors: [{ messageId: 'multiple' }] },
        { code: 'class Large {\n' + '\n'.repeat(350) + '}', errors: [{ messageId: 'length' }] }
    ]
});

tester.run('fetch-signal', quality.rules['fetch-signal'], {
    valid: [
        'fetch(url,{signal: controller.signal})',
        "fetch('/health',{signal: AbortSignal.timeout(1000)})",
        'function work(fetch){fetch(url);}',
        'fetch(url, options)'
    ],
    invalid: [
        { code: "fetch('/health')", errors: [{ messageId: 'signal' }] },
        { code: 'fetch(url)', errors: [{ messageId: 'signal' }] },
        { code: 'fetch(url,{signal:null})', errors: [{ messageId: 'signal' }] },
        { code: 'fetch(url,{signal:undefined})', errors: [{ messageId: 'signal' }] },
        { code: 'window.fetch(url,{})', errors: [{ messageId: 'signal' }] }
    ]
});

tester.run('async-name', quality.rules['async-name'], {
    valid: [
        'async function readAsync() {}',
        'items.forEach(async item=>use(item));',
        '/** 検索します。 @contract 既存のinterop名を維持します。 */ export async function read(){}'
    ],
    invalid: [
        { code: 'async function read(){}', errors: [{ messageId: 'name' }] },
        { code: 'const read=async()=>{};', errors: [{ messageId: 'name' }] },
        { code: '/** 読みます。 @contract fixed name */ async function read(){}', errors: [{ messageId: 'name' }] }
    ]
});

tester.run('conditional-strings', quality.rules['conditional-strings'], {
    valid: ['if(typeof value === "string") {}', 'if(value === "") {}', 'const open = "open"; if(state === open) {}', 'switch(state){case "open":break;}'],
    invalid: [
        { code: 'if(state === "open") {}', errors: [{ messageId: 'literal' }] },
        { code: 'const label = state === "open" ? a : b;', errors: [{ messageId: 'literal' }] },
        { code: 'while(state !== "closed") {}', errors: [{ messageId: 'literal' }] }
    ]
});

tester.run('statement-layout', quality.rules['statement-layout'], {
    valid: ['const one=1;\nconst two=2;', 'const template="a;b";', 'export class One {}\n\nexport default class Two {}',
        'class Reader {\n read() {}\n\n close() {}\n}', 'const item={read() {},\n\nclose() {}};',
        'const item={a:1,b:2};', 'function one() {} // 補足です。\n\nfunction two() {}'],
    invalid: [
        { code: 'const one=1; const two=2;', output: 'const one=1;\nconst two=2;', errors: [{ messageId: 'line' }] },
        { code: 'function one() {}\nfunction two() {}', output: 'function one() {}\n\nfunction two() {}', errors: [{ messageId: 'spacing' }] },
        { code: 'export class One {}\nexport default class Two {}', output: 'export class One {}\n\nexport default class Two {}', errors: [{ messageId: 'spacing' }] },
        { code: 'class Reader {\n read() {}\n close() {}\n}', output: 'class Reader {\n read() {}\n\n close() {}\n}', errors: [{ messageId: 'spacing' }] },
        { code: 'const item={read() {},\nclose() {}};', output: 'const item={read() {},\n\nclose() {}};', errors: [{ messageId: 'spacing' }] },
        { code: 'const item={read() {} /* 補足です。 */,\nclose() {}};', output: 'const item={read() {} /* 補足です。 */,\n\nclose() {}};', errors: [{ messageId: 'spacing' }] },
        { code: 'function one() {} // 補足です。\n/** 次の関数です。 */\nfunction two() {}', output: 'function one() {} // 補足です。\n\n/** 次の関数です。 */\nfunction two() {}', errors: [{ messageId: 'spacing' }] },
        { code: 'const one=()=>1;\nconst two=2;', output: 'const one=()=>1;\n\nconst two=2;', errors: [{ messageId: 'spacing' }] },
        { code: 'class Reader {\r\n read() {}\r\n close() {}\r\n}', output: 'class Reader {\r\n read() {}\r\n\r\n close() {}\r\n}', errors: [{ messageId: 'spacing' }] }
    ]
});

tester.run('comment-spacing', quality.rules['comment-spacing'], {
    valid: ['/** 説明です。 */\nrun();', 'function run(){\n// 先頭の説明です。\nuse();\n}',
        'run();\n\n// 説明です。\n// 続きです。\nuse();', 'run(); // 補足です。',
        'function run(){ // ブロックです。\n// 先頭の説明です。\nuse();\n}',
        'function run(){return /** 単位です。 */ 1;}', 'const text="// 文字列です。";'],
    invalid: [
        { code: 'run();\n// 説明です。\nuse();', output: 'run();\n\n// 説明です。\nuse();', errors: [{ messageId: 'spacing' }] },
        { code: 'function run(){\n\n// 先頭です。\nuse();\n}', output: 'function run(){\n// 先頭です。\nuse();\n}', errors: [{ messageId: 'spacing' }] },
        { code: 'function run(){ // ブロックです。\n\n// 先頭です。\nuse();\n}', output: 'function run(){ // ブロックです。\n// 先頭です。\nuse();\n}', errors: [{ messageId: 'spacing' }] },
        { code: '\n/** 説明です。 */\nrun();', output: '/** 説明です。 */\nrun();', errors: [{ messageId: 'spacing' }] },
        { code: 'const item={ /** メソッドです。 */\nrun(){}};', output: 'const item={\n /** メソッドです。 */\nrun(){}};', errors: [{ messageId: 'spacing' }] },
        { code: 'run();\r\n// 説明です。\r\nuse();', output: 'run();\r\n\r\n// 説明です。\r\nuse();', errors: [{ messageId: 'spacing' }] }
    ]
});

tester.run('compact-comment', quality.rules['compact-comment'], {
    valid: ['/** 保留した要求を完了します。 */', '/** 説明です。\n * @returns 結果です。\n */', '// 行コメントです。'],
    invalid: [
        { code: '/** 保留した要求を完了します。\n */', output: '/** 保留した要求を完了します。 */', errors: [{ messageId: 'compact' }] },
        { code: '/**\n * 説明です。\n */', output: '/** 説明です。 */', errors: [{ messageId: 'compact' }] },
        { code: '/* 理由です。\r\n */', output: '/* 理由です。 */', errors: [{ messageId: 'compact' }] }
    ]
});

describe('マークアップの実行境界', () =>
{
    it('外部script、JSON、コメント内のscriptを許可する', () =>
    {
        assert.deepEqual(inspectMarkup('<script src="app.js"></script><script type="application/json">{"a":1}</script><!-- <script>run()</script> -->'), []);
    });
    it('template内のscriptとイベント属性の元位置を返す', () =>
    {
        const found = inspectMarkup('<template>\n<script>run()</script>\n<button onclick="run()">実行</button></template>');
        assert.deepEqual(found.map(message => message.line), [2, 3]);
    });
    it('RazorのC#イベント接続をJavaScript属性とは混同しない', () =>
    {
        assert.deepEqual(inspectMarkup('<button @onclick="RunAsync">実行</button>'), []);
        assert.deepEqual(inspectMarkup('<InputFile OnChange="ReadAsync" /><span title="@Ui["only text"]"></span>', true), []);
        assert.equal(inspectMarkup('<INPUT onchange="run()">', true).length, 1);
        assert.equal(inspectMarkup('<input onchange="run()">', true).length, 1);
        assert.equal(inspectMarkup('<Custom-element onclick="run()"></Custom-element>', true).length, 1);
    });
});
