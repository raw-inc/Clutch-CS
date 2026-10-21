import test from 'node:test';
import assert from 'node:assert/strict';
import { analyzeCode } from '../../../src/ReArtWord.Contents.BackgroundService.Nodes/Scripting/JavaScript/javascript-contract.mjs';

test('JavaScriptのdefault引数とasync返却を実行せずに推定する', () =>
{
    const result = analyzeCode({ code: 'export async function run(left = 1, right = 2) { return { sum: left + right, text: `value` }; }' });
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.contract.arguments.left, { type: 'number', optional: true });
    assert.deepEqual(result.contract.result, { type: 'object', properties: { sum: { type: 'number' }, text: { type: 'string' } } });
});

test('import・default式・top-levelコードを型推定のために実行しない', () =>
{
    const result = analyzeCode({ code: "import absent from 'uninstalled-package'; throw new Error('must not execute'); export function run(value = absent()) { return absent(value); }" });
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.contract.arguments, { value: { type: 'any', optional: true } });
    assert.deepEqual(result.contract.result, { type: 'any' });
});

test('推定できないJSON入出力は明示型で確定できる', () =>
{
    const result = analyzeCode({ code: "import format from 'package'; export function run(input) { return format(input); }", manualContract: { arguments: { input: { type: 'string' } }, result: { type: 'string' } } });
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.contract, { arguments: { input: { type: 'string' } }, result: { type: 'string' } });
});

test('構文確認ではrunを要求せず、不正な構文の位置を返す', () =>
{
    assert.deepEqual(analyzeCode({ code: "import packageValue from 'package'; const value = packageValue();", syntaxOnly: true }), { contract: null, diagnostics: [] });
    const invalid = analyzeCode({ code: 'export function run() { return ;', syntaxOnly: true });
    assert.equal(invalid.contract, null);
    assert.match(invalid.diagnostics[0], /JavaScript syntax error/u);
});

for (const code of ['function run() { return 1; }', 'export function* run() { yield 1; }', 'export function run({ value }) { return 1; }'])
{
    test(`入口の曖昧な呼出しを拒否する: ${code}`, () =>
    {
        const result = analyzeCode({ code });
        assert.equal(result.contract, null);
        assert.notEqual(result.diagnostics.length, 0);
    });
}

test('入れ子の関数のreturnを入口の返却型へ混入しない', () =>
{
    const result = analyzeCode({ code: "export function run() { function helper() { return 1; } return 'text'; }" });
    assert.deepEqual(result.contract.result, { type: 'string' });
});

test('途中で再代入される引数の旧型から返却型を断定しない', () =>
{
    const result = analyzeCode({ code: "export function run(value = 1) { value = 'changed'; return value; }" });
    assert.deepEqual(result.contract.result, { type: 'any' });
    assert.deepEqual(result.diagnostics, []);
});

test('条件ごとに一致しない返却型はanyとして公開する', () =>
{
    const result = analyzeCode({ code: "export function run(value = true) { return value ? 'text' : 12; }" });
    assert.deepEqual(result.contract.result, { type: 'any' });
    assert.deepEqual(result.diagnostics, []);
});

test('未推定の引数はanyで受け、返却型だけを手動選択できる', () =>
{
    const result = analyzeCode({ code: 'export function run(value) { return value; }', manualContract: { result: { type: 'string' } } });
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.contract.arguments.value, { type: 'any' });
    assert.deepEqual(result.contract.result, { type: 'string' });
});

test('可変長引数と実行時だけ評価する既定値を公開する', () =>
{
    const result = analyzeCode({ code: 'export function run(first = sideEffect(), ...values) { return values; }' });
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.defaultArguments, ['first']);
    assert.equal(result.contract.variadicArgument, 'values');
    assert.deepEqual(result.contract.arguments.first, { type: 'any', optional: true });
    assert.deepEqual(result.contract.arguments.values, { type: 'array', elementType: { type: 'any' }, optional: true });
});
