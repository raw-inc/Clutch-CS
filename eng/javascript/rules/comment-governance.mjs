import { japanese } from '../ast.mjs';

/** 一括抑制を禁止し、保留と想定した無視には理由を求めます。 */
export default {
    meta: { type: 'problem', schema: [], messages: {
        suppression: 'インラインのESLint設定・一括抑制は禁止です。適用範囲は設定ファイルで定義してください。',
        todo: 'TODO/FIXME/FYIには日本語の理由と解消条件を記載してください。',
        catch: '空のcatchには、その失敗を無視して継続できる日本語の理由を記載してください。'
    } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        return {
            /** ファイル全体のコメントと文の配置を検査します。 */
            Program()
            {
                for (const comment of context.sourceCode.getAllComments())
                {
                    if (/^\s*(?:eslint|global(?:s)?\b|exported\b)/u.test(comment.value))
                    {
                        context.report({ loc: comment.loc, messageId: 'suppression' });
                    }
                    for (const line of comment.value.split(/\r?\n/u))
                    {
                        if (/^\s*\*?\s*(?:@todo\b|TODO\b|FIXME\b|FYI\b)/iu.test(line) && !japanese(line))
                        {
                            context.report({ loc: comment.loc, messageId: 'todo' });
                        }
                    }
                }
            },

            /** 空のcatchに継続可能な理由があるかを検査します。 */
            CatchClause(node)
            {
                if (node.body.body.length === 0 && !context.sourceCode.getCommentsInside(node.body).some(comment => japanese(comment.value)))
                {
                    context.report({ node, messageId: 'catch' });
                }
            }
        };
    }
};
