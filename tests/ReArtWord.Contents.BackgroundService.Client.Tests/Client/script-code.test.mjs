import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseFragment } from 'parse5';
import { highlightCode } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/script-code.js';
import { editorIndent, editorLanguage } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/code-editor-languages.js';
import { editorCompletions } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/code-editor-completions.js';
import { EditorState, CompletionContext } from '../../../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/code-editor/codemirror.js';

/** 表示用HTMLを解釈し、元の文字列と生成された字句を確認します。
 * @returns 文字列と色分けされた字句です。
 */
function inspectHighlight(code, language)
{
    const tokens = [];

    /** テキスト以外に生成できる要素をspanとclass属性だけに限定します。
     * @returns その要素が表示する文字列です。
     */
    function read(node)
    {
        if (node.nodeName === '#text')
        {
            return node.value;
        }
        if (node.tagName)
        {
            assert.equal(node.tagName, 'span');
            assert.deepEqual(node.attrs.map(attribute => attribute.name), ['class']);
        }
        const text = (node.childNodes ?? []).map(read).join('');
        if (node.tagName)
        {
            tokens.push({ kind: node.attrs[0].value, text });
        }
        return text;
    }

    const text = read(parseFragment(highlightCode(code, language)));
    assert.equal(text, code.replaceAll('\r\n', '\n').replaceAll('\r', '\n'));
    return tokens;
}

/** 各言語で日常的に使う構文と期待する表示区分です。 */
const examples = [
    ['javascript', 'const pattern = /a+/gi;\nconst message = `hello ${name}`; // comment', 'code-keyword', 'const'],
    ['bash', '#!/bin/bash\nif [ "$USER" = "test" ]; then\n  echo "日本語"\nfi', 'code-keyword', 'then'],
    ['powershell', '<# multiline\ncomment #>\nparam($Name)\nWrite-Host "$Name" -ForegroundColor Green', 'code-keyword', 'param'],
    ['json', '{"message":"日本語", "enabled":true, "count":-1.25e2}', 'code-attr', '"message"'],
    ['toml', '[server]\nname = "日本語"\nenabled = true\nports = [80, 443] # comment', 'code-attr', 'name'],
    ['yaml', 'defaults: &defaults\n  enabled: true\ncopy: *defaults\nmessage: |\n  日本語\n  content', 'code-attr', 'defaults:']
];

for (const [language, source, kind, token] of examples)
{
    test(`${language}の色分けは元のテキストを保持する`, () =>
    {
        const tokens = inspectHighlight(source, language);
        assert.ok(tokens.some(item => item.kind === kind && item.text === token), JSON.stringify(tokens));
    });
}

test('複数行文字列・コメント・変数・数値も色分けする', () =>
{
    assert.ok(inspectHighlight('const x = /x+/g;', 'javascript').some(token => token.kind === 'code-regexp'));
    assert.ok(inspectHighlight('echo "$HOME" # comment', 'bash').some(token => token.kind === 'code-variable'));
    assert.ok(inspectHighlight('<# comment\nnext #>\n$Name = @"\ntext\n"@', 'powershell').some(token => token.kind === 'code-comment' && token.text.includes('\n')));
    assert.ok(inspectHighlight('message = """first\nsecond"""', 'toml').some(token => token.kind === 'code-string' && token.text.includes('\n')));
    assert.ok(inspectHighlight('{"n": -2.4e+3}', 'json').some(token => token.kind === 'code-number' && token.text === '-2.4e+3'));
    assert.ok(inspectHighlight('message: |\n  first\n  second\n', 'yaml').some(token => token.kind === 'code-string' && token.text.includes('second')));
    assert.equal(highlightCode('$name = 1', 'ps1'), highlightCode('$name = 1', 'powershell'));
});

test('編集中の未完了構文とHTML入力を全言語でそのまま表示する', () =>
{
    for (const language of ['csharp', 'python', ...examples.map(example => example[0])])
    {
        for (const source of ['', '"unfinished\n日本語', '</span><img src=x onerror="globalThis.executed=true">&\r\n', '"\\'.repeat(2000)])
        {
            inspectHighlight(source, language);
        }
    }
    for (const language of ['text', 'unknown', '__proto__', 'constructor'])
    {
        assert.equal(highlightCode('<a>&"', language), '&lt;a&gt;&amp;&quot;');
    }
});

test('highlights C# and Python while escaping all user markup', () =>
{
    const csharp = highlightCode('public string Run() => "<img src=x onerror=alert(1)>"; // return', 'csharp');
    assert.match(csharp, /class="code-keyword">public<\/span>/);
    assert.match(csharp, /class="code-string">&quot;&lt;img/);
    assert.match(csharp, /class="code-comment">\/\/ return<\/span>/);
    assert.ok(!csharp.includes('<img'));
    const python = highlightCode('def run(x):\n    return """class\n<svg onload=bad()>""" # pass', 'python');
    assert.match(python, /class="code-keyword">def<\/span>/);
    assert.match(python, /class="code-string">&quot;&quot;&quot;class/);
    assert.ok(!python.includes('<svg'));
    assert.equal(highlightCode('<a>&"', 'text'), '&lt;a&gt;&amp;&quot;');
});

test('既存の字下げ幅とタブを維持し、新規言語に既定幅を適用する', () =>
{
    assert.equal(editorIndent('if (true) {\n  return 1;\n}', 'csharp'), '  ');
    assert.equal(editorIndent('def run():\n\treturn 1', 'python'), '\t');
    assert.equal(editorIndent('', 'yaml'), '  ');
    assert.equal(editorIndent('', 'javascript'), '    ');
});

test('補完は文中の識別子・標準メンバーを候補にし、コメント内を除外する', () =>
{
    for (const [language, code, label] of [['csharp', 'Console.Wr', 'WriteLine'], ['javascript', 'const localValue=1; localV', 'localValue'], ['python', 'variable_name = 1\nvari', 'variable_name']])
    {
        const state = EditorState.create({ doc: code, extensions: [editorLanguage(language)] });
        const result = editorCompletions(language)(new CompletionContext(state, code.length, true));
        assert.ok(result.options.some(option => option.label === label));
    }
    const state = EditorState.create({ doc: '// Console.Wr', extensions: [editorLanguage('csharp')] });
    assert.equal(editorCompletions('csharp')(new CompletionContext(state, state.doc.length, false)), null);
});
