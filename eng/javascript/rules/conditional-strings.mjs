/** 業務上の判定値を名前で説明し、typeofの固定語彙とは区別します。 */
export default {
    meta: { type: 'suggestion', schema: [], messages: { literal: '条件内の文字列比較値には意味を表す名前を付けてください。' } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        return {
            /** 二項演算の比較方向と判定値を検査します。 */
            BinaryExpression(node)
            {
                if (!['===', '!==', '==', '!='].includes(node.operator)
                    || [node.left, node.right].some(value => value.type === 'UnaryExpression' && value.operator === 'typeof'))
                {
                    return;
                }
                const literals = [node.left, node.right].filter(value => value.type === 'Literal' && typeof value.value === 'string' && value.value.length);
                if (!literals.length)
                {
                    return;
                }
                let current = node;
                for (let parent = node.parent; parent; current = parent, parent = parent.parent)
                {
                    if (parent.test === current && ['IfStatement', 'WhileStatement', 'DoWhileStatement', 'ForStatement', 'ConditionalExpression'].includes(parent.type))
                    {
                        for (const literal of literals)
                        {
                            context.report({ node: literal, messageId: 'literal' });
                        }
                        return;
                    }
                    if (parent.type.endsWith('Statement') || parent.type.endsWith('FunctionExpression') || parent.type === 'FunctionDeclaration')
                    {
                        return;
                    }
                }
            }
        };
    }
};
