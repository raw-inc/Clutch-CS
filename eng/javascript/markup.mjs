import { parse, html } from 'parse5';

/** HTMLの位置情報を保ち、実行コードが外部ファイルへ分離されているかを確認します。
 * @returns ESLint形式の診断です。
 */
export function inspectMarkup(text, components = false)
{
    const messages = [];

    /** HTMLとtemplateの要素を辿り、実行可能な属性とscriptだけを検査します。 */
    function visit(node)
    {
        const location = node.sourceCodeLocation;
        if (location && node.tagName)
        {
            const attributes = new Map(node.attrs.map(attribute => [attribute.name, attribute.value]));
            const component = components && /^<[A-Z]/u.test(text.slice(location.startOffset))
                && html.getTagID(node.tagName) === html.TAG_ID.UNKNOWN && !node.tagName.includes('-') && node.namespaceURI === html.NS.HTML;
            const type = (attributes.get('type') ?? '').trim().toLowerCase();
            if (node.tagName === 'style')
            {
                report(location, 'CSSの定義は共通またはコンポーネントの外部CSSへ分離してください。', 'quality/style-boundary');
            }
            const style = attributes.get('style');
            if (!component && style?.trim() && (!components || !style.includes('@')))
            {
                report(location.attrs.style, '固定のインラインCSSは外部CSSのクラスへ移してください。動的な座標・寸法は維持できます。', 'quality/style-boundary');
            }
            if (node.tagName === 'script' && !attributes.has('src') && ['', 'module', 'text/javascript', 'application/javascript', 'text/ecmascript', 'application/ecmascript'].includes(type)
                && node.childNodes.some(child => child.value?.trim()))
            {
                report(location, 'JavaScriptの実行処理は外部ファイルへ分離してください。');
            }
            for (const attribute of node.attrs)
            {
                if (!component && /^on[a-z]+$/u.test(attribute.name) && attribute.value.trim())
                {
                    report(location.attrs[attribute.name], 'インラインのイベント処理はJavaScriptのaddEventListenerへ分離してください。');
                }
            }
        }
        for (const child of node.childNodes ?? [])
        {
            visit(child);
        }
        if (node.content)
        {
            visit(node.content);
        }
    }

    /** パーサーの元位置から診断を作成します。 */
    function report(location, message, ruleId = 'quality/markup-boundary')
    {
        messages.push({ ruleId, severity: 2, message, line: location.startLine, column: location.startCol });
    }

    visit(parse(text, { sourceCodeLocationInfo: true }));
    return messages;
}

/** マークアップの検査結果だけをESLintへ返すprocessorです。 */
export default {
    meta: { name: 'markup-boundary', version: '1.0.0' },

    /** ESLintのJavaScriptパーサーへは、検査済みの空モジュールを渡します。
     * @returns 構文検査の対象となる空のソースです。
     */
    preprocess(text, filename)
    {
        pending.set(filename, inspectMarkup(text, filename.endsWith('.razor')));
        return [''];
    },

    /** 一回の検査結果を返し、ファイルの内容を保持し続けません。
     * @returns マークアップ上の診断です。
     */
    postprocess(messages, filename)
    {
        const result = pending.get(filename) ?? [];
        pending.delete(filename);
        return [...messages.flat(), ...result];
    }
};

/** 一回のESLint呼出し内でpreprocessとpostprocessを結ぶ結果です。 */
const pending = new Map();
