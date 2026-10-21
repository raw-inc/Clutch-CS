import { declaration, documentation, japanese } from '../ast.mjs';

/** 名前を持つ関数・クラスの責務と戻り値を日本語で説明させます。 */
export default {
    meta: { type: 'suggestion', schema: [], messages: { summary: '宣言へ日本語のJSDoc概要を付けてください。', returns: '戻り値を日本語の@returnsで説明してください。' } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        const stack = [];

        /** 関数ごとの戻り値を、入れ子の関数と分離して記録します。 */
        function enter(node)
        {
            stack.push({ node, returns: node.async || (node.type === 'ArrowFunctionExpression' && node.expression) });
        }

        /** 関数とクラスの説明を確認します。 */
        function inspect(node, returns)
        {
            const target = declaration(node);
            if (!target)
            {
                return;
            }
            const doc = documentation(context.sourceCode, target);
            if (!doc || !japanese(doc.split('@')[0]))
            {
                context.report({ node: target, messageId: 'summary' });
                return;
            }
            if (returns && !/@returns?\b[^\n]*[\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Han}]/u.test(doc))
            {
                context.report({ node: target, messageId: 'returns' });
            }
        }

        /** 関数自身が返す値だけを検査します。 */
        function leave(node)
        {
            inspect(node, stack.pop().returns);
        }

        return {
            ':function': enter,
            ':function:exit': leave,

            /** 現在の関数が値を返すことを記録します。 */
            ReturnStatement(node)
            {
                if (node.argument && stack.length)
                {
                    stack.at(-1).returns = true;
                }
            },

            /** クラス宣言の責務・規模・説明を検査します。 */
            'ClassDeclaration, ClassExpression'(node)
            {
                inspect(node, false);
            }
        };
    }
};
