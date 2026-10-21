/** 同じブロックの文を行で分け、宣言とその説明を一つのまとまりにします。 */
export default {
    meta: { type: 'layout', fixable: 'whitespace', schema: [], messages: { line: '一つの行へ複数の文を詰め込まないでください。', spacing: '宣言の区切りには空行を1行置いてください。' } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        const source = context.sourceCode;

        /** 関数またはクラスの宣言を判定します。
         * @returns 宣言の前後を空行で分ける場合はtrueです。
         */
        function boundary(node)
        {
            const declaration = node.declaration ?? node;
            return ['FunctionDeclaration', 'ClassDeclaration'].includes(declaration.type)
                || (declaration.type === 'VariableDeclaration' && declaration.declarations.some(item => ['FunctionExpression', 'ArrowFunctionExpression', 'ClassExpression'].includes(item.init?.type)));
        }

        /** メソッドと関数を持つメンバーを判定します。
         * @returns メソッド境界の空行を必要とする場合はtrueです。
         */
        function method(node)
        {
            return node.type === 'MethodDefinition' || node.type === 'StaticBlock'
                || ['FunctionExpression', 'ArrowFunctionExpression'].includes(node.value?.type);
        }

        /** コメントの所属を保ちながら隣り合う文やメソッドを分けます。 */
        function inspect(statements, members = false)
        {
            for (let index = 1; index < statements.length; index++)
            {
                const before = statements[index - 1], after = statements[index];
                const separated = members ? method(before) || method(after) : boundary(before) || boundary(after);
                if (members && !separated)
                {
                    continue;
                }
                let left = before, right = after;
                const separator = source.getTokenAfter(before);
                if (members && separator?.value === ',')
                {
                    left = separator;
                }
                const comments = source.getCommentsBefore(after).filter(comment => left.range[1] <= comment.range[0]);
                for (const comment of comments)
                {
                    if (comment.loc.start.line === left.loc.end.line && !(comment.type === 'Block' && comment.value.startsWith('*')))
                    {
                        left = comment;
                    }
                    else
                    {
                        right = comment;
                        break;
                    }
                }
                const gap = source.text.slice(left.range[1], right.range[0]);
                if (!/^\s*$/u.test(gap))
                {
                    continue;
                }
                const indentation = gap.includes('\n') ? gap.match(/[^\S\r\n]*$/u)[0] : '';
                const newline = gap.includes('\r\n') ? '\r\n' : '\n';
                if (separated && right.loc.start.line - left.loc.end.line !== 2)
                {
                    context.report({ node: after, messageId: 'spacing',

                        fix: fixer => fixer.replaceTextRange([left.range[1], right.range[0]], newline.repeat(2) + indentation) });
                }
                else if (left.loc.end.line === right.loc.start.line)
                {
                    context.report({ node: after, messageId: 'line',

                        fix: fixer => fixer.replaceTextRange([left.range[1], right.range[0]], newline + indentation) });
                }
            }
        }

        return {
            /** ファイル直下の文を読みやすい行へ分けます。 */
            Program(node)
            {
                inspect(node.body);
            },

            /** ブロック内の文を読みやすい行へ分けます。 */
            BlockStatement(node)
            {
                inspect(node.body);
            },

            /** case内の文を読みやすい行へ分けます。 */
            SwitchCase(node)
            {
                inspect(node.consequent);
            },

            /** クラスのメソッドや初期化ブロックの境界を分けます。 */
            ClassBody(node)
            {
                inspect(node.body, true);
            },

            /** オブジェクトに定義したメソッドの境界を分けます。 */
            ObjectExpression(node)
            {
                inspect(node.properties, true);
            }
        };
    }
};
