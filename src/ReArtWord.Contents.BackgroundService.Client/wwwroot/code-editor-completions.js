import { completeAnyWord, javascriptLocals, pythonLocals, syntaxTree } from './vendor/code-editor/codemirror.js';
import { simpleCompletion } from './code-editor-insertion.js';

/** 言語が補完を提供しない箇所にも、一般的な構文候補を用意します。 */
const words = new Map([
    ['csharp', 'public private protected internal static async await return if else switch case default for foreach while break continue try catch finally throw using namespace class record struct interface enum new var const readonly null true false string int long float double decimal bool object Task Console Math DateTime Guid List Dictionary field'],
    ['python', 'def return if elif else for while in import from as class try except finally raise with async await yield pass break continue True False None print len range str int float list dict set tuple'],
    ['javascript', 'export import from async await function return const let if else for of while switch case break continue try catch finally throw new true false null undefined console Math JSON Promise Object Array String Number'],
    ['bash', 'if then elif else fi for in do done while case esac function echo printf read export local return exit set test'],
    ['powershell', 'param function if elseif else foreach for while switch try catch finally return throw Write-Output Write-Host Get-Content Set-Content Get-Item Get-ChildItem Join-Path Test-Path ConvertFrom-Json ConvertTo-Json Where-Object ForEach-Object'],
    ['json', 'true false null'], ['toml', 'true false'], ['yaml', 'true false null']
]);

/** 型解析を実行せず提示できる、標準ライブラリーの代表的なメンバーです。 */
const members = new Map([
    ['csharp:Console', 'WriteLine Write ReadLine'], ['csharp:Math', 'Abs Min Max Round Floor Ceiling Sqrt Pow'],
    ['csharp:Task', 'Delay WhenAll WhenAny FromResult'], ['csharp:string', 'IsNullOrEmpty IsNullOrWhiteSpace Join Format'],
    ['javascript:console', 'log warn error info'], ['javascript:JSON', 'parse stringify'],
    ['javascript:Math', 'abs min max round floor ceil random sqrt pow'], ['javascript:Promise', 'resolve reject all allSettled race'],
    ['python:json', 'loads dumps load dump'], ['python:os', 'path environ getcwd']
]);

/** ローカル宣言は文法から取得し、引数や本体を含む標準スニペットは追加しません。 */
const locals = new Map([['javascript', javascriptLocals], ['python', pythonLocals]]);

/** 名前の補完だけを行う入口メソッドです。 */
const entries = new Map([['csharp', 'Run'], ['python', 'run'], ['javascript', 'run']]);

/** モジュールや辞書のプロパティへ呼出し括弧を補いません。 */
const memberValues = new Set(['python:os:path', 'python:os:environ']);

/** コメントや文字列内で一般コードの候補を自動挿入しません。
 * @returns コード候補を表示できる位置かです。
 */
function codePosition(context)
{
    return !/(?:comment|string)/iu.test(syntaxTree(context.state).resolveInner(context.pos, -1).name);
}

/** 言語候補・標準メンバー・現在の文書にある識別子を合成します。
 * @returns CodeMirrorの補完ソースです。
 */
export function editorCompletions(language)
{
    const alias = 'ps1';
    const selected = language === alias ? 'powershell' : language;
    const options = (words.get(selected) ?? '').split(' ').filter(Boolean).map(label => ({ label, type: 'keyword' }));
    if (entries.has(selected))
    {
        options.push({ label: entries.get(selected), type: 'function' });
    }
    return context =>
    {
        if (!codePosition(context))
        {
            return null;
        }
        const word = context.matchBefore(/[\w$-]*/u);
        const owner = context.matchBefore(/([\w]+)\.[\w]*$/u)?.text.split('.')[0];
        const known = members.get(`${selected}:${owner}`);
        if (!word || word.from === word.to && !context.explicit && !known)
        {
            return null;
        }
        if (known)
        {
            return { from: word.from, options: known.split(' ').map(label => simpleCompletion(selected, { label, type: memberValues.has(`${selected}:${owner}:${label}`) ? 'property' : 'function' })), validFor: /^[\w]*$/u };
        }
        const local = completeAnyWord(context);
        const declared = locals.get(selected)?.(context);
        const merged = new Map([...(local?.options ?? []), ...options, ...(declared?.options ?? [])].map(option => [option.label, option]));
        return { from: word.from, options: [...merged.values()].map(option => simpleCompletion(selected, option)), validFor: /^[\w$-]*$/u };
    };
}
