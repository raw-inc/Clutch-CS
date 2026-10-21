/** クラスの命名・行数・ファイル内の責務を検査します。 */
export default {
    meta: { type: 'suggestion', schema: [], messages: {
        length: 'クラスは350行以内に分けてください。',
        name: 'クラス名はPascalCaseにしてください。',
        multiple: 'トップレベルのクラスは1ファイルに1つまでです。'
    } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        let count = 0;
        return {
            /** クラス宣言の責務・規模・説明を検査します。 */
            'ClassDeclaration, ClassExpression'(node)
            {
                if (350 < node.loc.end.line - node.loc.start.line + 1)
                {
                    context.report({ node, messageId: 'length' });
                }
                const identifier = node.id ?? (node.parent.type === 'VariableDeclarator' ? node.parent.id : null);
                if (identifier?.type === 'Identifier' && !/^[A-Z][a-zA-Z0-9]*$/u.test(identifier.name))
                {
                    context.report({ node: identifier, messageId: 'name' });
                }
                let parent = node.parent.type === 'VariableDeclarator' ? node.parent.parent.parent : node.parent;
                if (parent.type.startsWith('Export'))
                {
                    parent = parent.parent;
                }
                if (parent.type === 'Program' && 1 < ++count)
                {
                    context.report({ node, messageId: 'multiple' });
                }
            }
        };
    }
};
