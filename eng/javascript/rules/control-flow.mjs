/** 比較方向・無条件ループ・長い条件連鎖を検査します。 */
export default {
    meta: { type: 'suggestion', fixable: 'code', schema: [], messages: {
        direction: '小さい値を左に置く < / <= で比較してください。',
        loop: 'forの終了条件を明示してください。',
        chain: '3つ以上のelse-if連鎖をswitchまたは責務ごとの処理へ整理してください。',
        ternary: '三項演算子の条件数は5未満にしてください。'
    } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        return {
            /** 二項演算の比較方向と判定値を検査します。 */
            BinaryExpression(node)
            {
                if (node.operator !== '>' && node.operator !== '>=')
                {
                    return;
                }
                const safe = [node.left, node.right].some(value => value.type === 'Literal' && !value.regex);
                context.report({ node, messageId: 'direction', fix: safe ? fixer => fixer.replaceText(node,
                    `(${context.sourceCode.getText(node.right)}) ${node.operator === '>' ? '<' : '<='} (${context.sourceCode.getText(node.left)})`) : null });
            },

            /** for文の終了条件が省略されていないかを検査します。 */
            ForStatement(node)
            {
                if (!node.test)
                {
                    context.report({ node, messageId: 'loop' });
                }
            },

            /** 先頭のifから条件連鎖の長さを検査します。 */
            IfStatement(node)
            {
                if (node.parent.type === 'IfStatement' && node.parent.alternate === node)
                {
                    return;
                }
                let count = 0;
                for (let next = node.alternate; next?.type === 'IfStatement'; next = next.alternate)
                {
                    count++;
                }
                if (3 <= count)
                {
                    context.report({ node, messageId: 'chain' });
                }
            },

            /** 三項演算子が含む判定の数を検査します。 */
            ConditionalExpression(node)
            {
                if (node.parent.type === 'ConditionalExpression')
                {
                    return;
                }
                if (5 <= conditions(node))
                {
                    context.report({ node, messageId: 'ternary' });
                }
            }
        };
    }
};

/** 三項演算子と論理演算子が持つ判定数を数えます。
 * @returns 判定の数です。
 */
function conditions(node)
{
    if (node.type === 'ConditionalExpression')
    {
        return conditions(node.test) + (node.consequent.type === 'ConditionalExpression' ? conditions(node.consequent) : 0)
            + (node.alternate.type === 'ConditionalExpression' ? conditions(node.alternate) : 0);
    }
    return node.type === 'LogicalExpression' ? conditions(node.left) + conditions(node.right) : 1;
}
