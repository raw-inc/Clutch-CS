import core from './vendor/highlight/core.min.js';
import csharp from './vendor/highlight/languages/csharp.min.js';
import python from './vendor/highlight/languages/python.min.js';
import javascript from './vendor/highlight/languages/javascript.min.js';
import bash from './vendor/highlight/languages/bash.min.js';
import powershell from './vendor/highlight/languages/powershell.min.js';
import json from './vendor/highlight/languages/json.min.js';
import toml from './vendor/highlight/languages/ini.min.js';
import yaml from './vendor/highlight/languages/yaml.min.js';

/** 他の画面の登録状態を変更せず、選択した言語だけを表示します。 */
const highlighter = core.newInstance();
highlighter.configure({ classPrefix: 'code-' });
for (const [name, grammar] of Object.entries({ csharp, python, javascript, bash, powershell, json, toml, yaml }))
{
    highlighter.registerLanguage(name, grammar);
}

/** プレーンテキストは呼出し側へ返し、登録済みの言語を実行せずに色分けします。
 * @returns HTMLをエスケープした色分け結果、または未対応を示すnullです。
 */
export function highlightLanguage(code, language)
{
    return highlighter.getLanguage(language) ? highlighter.highlight(code, { language, ignoreIllegals: true }).value : null;
}
