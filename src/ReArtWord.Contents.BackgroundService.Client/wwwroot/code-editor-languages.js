import { javascriptLanguage, pythonLanguage, json, yaml, StreamLanguage, csharp, powerShell, shell, toml, HighlightStyle, tags, indentService, syntaxTree } from './vendor/code-editor/codemirror.js';

/** 文法は編集インスタンス間で共有し、状態は各エディターが所有します。 */
const languages = new Map([
    ['javascript', javascriptLanguage], ['python', pythonLanguage], ['json', json()], ['yaml', yaml()],
    ['csharp', StreamLanguage.define(csharp)], ['powershell', StreamLanguage.define(powerShell)],
    ['ps1', StreamLanguage.define(powerShell)], ['bash', StreamLanguage.define(shell)], ['toml', StreamLanguage.define(toml)]
]);

/** 色の値は既存のCSSが所有し、文法のタグだけを対応付けます。 */
export const codeHighlight = HighlightStyle.define([
    { tag: tags.keyword, class: 'code-keyword' }, { tag: tags.string, class: 'code-string' },
    { tag: tags.number, class: 'code-number' }, { tag: tags.comment, class: 'code-comment' },
    { tag: tags.bool, class: 'code-literal' }, { tag: tags.null, class: 'code-literal' },
    { tag: tags.typeName, class: 'code-title' }, { tag: tags.propertyName, class: 'code-property' },
    { tag: tags.variableName, class: 'code-variable' }, { tag: tags.regexp, class: 'code-regexp' }
]);

/** 未対応言語やプレーンテキストへ文法を誤適用しません。
 * @returns 言語の編集支援、または空の拡張です。
 */
export function editorLanguage(language)
{
    return languages.get(language) ?? [];
}

/** 既存の字下げを優先し、新規の構造化データは2、それ以外は4スペースにします。
 * @returns 一段の字下げに使用する文字列です。
 */
export function editorIndent(text, language)
{
    const leading = text.split('\n').slice(0, 200).map(line => /^(\s+)\S/u.exec(line)?.[1]).filter(Boolean);
    if (leading.some(value => value.includes('\t')))
    {
        return '\t';
    }
    const sizes = leading.map(value => value.length).filter(length => (2) <= (length) && (length) <= (8));
    const fallback = ['json', 'yaml', 'toml'].includes(language) ? 2 : 4;
    return ' '.repeat(sizes.length ? Math.min(...sizes) : fallback);
}

/** 文法側に改行の字下げがない言語のブロック境界を補います。
 * @returns 改行位置に限定した字下げ規則です。
 */
export function editorIndentation(language)
{
    const modes = Object.freeze({ yaml: 'yaml', shell: 'bash', power: 'powershell', alias: 'ps1' });
    if (!Object.values(modes).includes(language))
    {
        return [];
    }
    return indentService.of((context, position) =>
    {
        if (context.simulatedBreak == null)
        {
            return null;
        }
        const line = context.lineAt(position, -1);
        const before = line.text.slice(0, position - line.from).replace(/\s+#.*$/u, '').trimEnd();
        const token = syntaxTree(context.state).resolveInner(line.from + before.length, -1).name;
        if (/(?:comment|string)/iu.test(token))
        {
            return null;
        }
        const base = context.countColumn(/^\s*/u.exec(before)[0]);
        if (language === modes.yaml && /(?::|[|>][+-]?)$/u.test(before))
        {
            return base + context.unit * (/^\s*-\s/u.test(before) ? 2 : 1);
        }
        if (language === modes.shell && /(?:\b(?:then|do|else)|\bcase\b.*\bin|[({])$/u.test(before))
        {
            return base + context.unit;
        }
        if ([modes.power, modes.alias].includes(language) && /[{[(]$/u.test(before))
        {
            return base + context.unit;
        }
        return null;
    });
}
