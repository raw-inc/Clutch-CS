import { insertCompletionText, syntaxTree } from './vendor/code-editor/codemirror.js';

/** 次に名前や式を記入する語だけ、確定時に区切りを一つ補います。 */
const spaced = new Map([
    ['csharp', 'public private protected internal static async await return using namespace class record struct interface enum new var const readonly string int long float double decimal bool object if else switch case for foreach while throw'],
    ['javascript', 'export import from async await function return const let var if else for of while switch case throw new class extends'],
    ['python', 'def async await return if elif else for while in import from as class except raise with yield'],
    ['bash', 'if then elif else for in do while case function echo printf read export local return exit set test'],
    ['powershell', 'param function if elseif else foreach for while switch return throw Write-Output Write-Host Get-Content Set-Content Get-Item Get-ChildItem Join-Path Test-Path ConvertFrom-Json ConvertTo-Json Where-Object ForEach-Object']
].map(([language, words]) => [language, new Set(words.split(' '))]));

/** 宣言であることを確認できる接頭辞に限定し、通常の変数宣言ではTabの字下げを維持します。 */
const declarations = new Map([
    ['csharp', /^\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|new)\s+)+[\w.]+(?:<[^\n<>]+>)?[?[\]]*\s+([A-Za-z_]\w*)$/u],
    ['javascript', /^\s*(?:(?:export|default|async)\s+)*function\s+([A-Za-z_$][\w$]*)$/u],
    ['python', /^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)$/u]
]);

/** 既存の括弧や空白を重複させず、一回のUndoで戻せる最小編集を行います。 */
function insert(view, label, from, to, callable, space)
{
    const after = view.state.sliceDoc(to, Math.min(to + 256, view.state.doc.length));
    let text = label, cursor = from + label.length;
    if (callable)
    {
        const opening = /^[ \t]*\(/u.exec(after);
        text += opening ? '' : '()';
        cursor += opening ? opening[0].length : 1;
    }
    else if (space)
    {
        const separator = /^[ \t]+/u.exec(after);
        text += separator ? '' : ' ';
        cursor += separator ? separator[0].length : 1;
    }
    view.dispatch({ ...insertCompletionText(view.state, text, from, to), selection: { anchor: cursor }, scrollIntoView: true });
}

/** 補完候補は語と空の括弧だけにし、仮の引数・返却値・本体を挿入しません。
 * @returns 言語に対応した最小挿入を持つ候補です。
 */
export function simpleCompletion(language, option)
{
    const callable = ['csharp', 'javascript', 'python'].includes(language) && option.type === 'function';
    return { ...option,

        /** 候補の確定位置へ最小限の補助を挿入します。 */
        apply(view, completion, from, to)
        {
            insert(view, completion.label, from, to, callable, spaced.get(language)?.has(completion.label));
        } };
}

/** 新しく記入したメソッド名でも、宣言末尾のTabで括弧内へ進めます。
 * @returns 宣言の補助を適用したかです。
 */
export function completeDeclaration(view, language)
{
    const selection = view.state.selection.main;
    if (!selection.empty || view.state.readOnly)
    {
        return false;
    }
    const line = view.state.doc.lineAt(selection.head), before = line.text.slice(0, selection.head - line.from);
    const name = declarations.get(language)?.exec(before)?.[1];
    const token = syntaxTree(view.state).resolveInner(selection.head, -1).name;
    if (!name || /(?:comment|string)/iu.test(token) || /^\s*(?:return|throw|new|await)\s/u.test(before))
    {
        return false;
    }
    insert(view, name, selection.head - name.length, selection.head, true, false);
    return true;
}
