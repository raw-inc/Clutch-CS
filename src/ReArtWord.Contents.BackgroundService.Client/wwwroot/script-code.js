import { highlightLanguage } from './script-code-languages.js';
import { attachEditor } from './code-editor-view.js';

/** 入力文字列をHTMLとして解釈されない表現へ変換します。
 * @returns 呼出し元が使用する値です。
 */
const escape = text => text.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));

/** 入力されたコードを安全な色分け表示へ変換します。
 * @returns 呼出し元が使用する値です。
 */
export function highlightCode(code, language)
{
    return highlightLanguage(code, language) ?? escape(code);
}

/** 専用エディターと、草稿へ保存するネイティブ入力を接続します。
 * @returns update・disposeを含む結果です。
 */
export function attachCodeEditor(input, preview, language)
{
    return attachEditor(input, preview, language);
}
