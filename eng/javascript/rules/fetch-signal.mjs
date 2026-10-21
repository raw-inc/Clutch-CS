/** ブラウザーのfetchへ所有期間または期限の取消信号を渡させます。 */
export default {
    meta: { type: 'problem', schema: [], messages: { signal: 'fetchへ所有期間またはタイムアウトを表すsignalを明示してください。' } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        return {
            /** 標準fetchに取消信号が渡されているかを検査します。 */
            CallExpression(node)
            {
                const direct = node.callee.type === 'Identifier' && node.callee.name === 'fetch';
                const qualified = node.callee.type === 'MemberExpression' && !node.callee.computed && node.callee.property.name === 'fetch'
                    && node.callee.object.type === 'Identifier' && ['window', 'self', 'globalThis'].includes(node.callee.object.name);
                if (!direct && !qualified)
                {
                    return;
                }
                let scope = context.sourceCode.getScope(node);
                while (scope && !scope.set.has('fetch'))
                {
                    scope = scope.upper;
                }
                if (direct && scope?.set.get('fetch').defs.length)
                {
                    return;
                }
                const options = node.arguments[1];
                if (options?.type === 'ObjectExpression' && options.properties.some(property => property.type === 'Property'
                    && (property.key.name ?? property.key.value) === 'signal' && property.value.type !== 'Literal'
                    && !(property.value.type === 'Identifier' && property.value.name === 'undefined')))
                {
                    return;
                }

                // 変数で渡すoptionsの内容は、呼出し箇所だけから欠落を断定しません。
                if (options && options.type !== 'ObjectExpression')
                {
                    return;
                }
                context.report({ node, messageId: 'signal' });
            }
        };
    }
};
