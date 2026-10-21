/** 本文が一行のブロックコメントは、閉じ記号まで同じ行に収めます。 */
export default {
    meta: {
        type: 'layout', fixable: 'whitespace', schema: [],
        messages: { compact: '本文が1行のコメントは閉じ記号 */ まで同じ行にしてください。' }
    },

    /** コメントの内容行を確認する検査を構成します。
     * @returns ファイル内のコメントを検査する処理です。
     */
    create(context)
    {
        return {
            /** 内容が複数行の説明やタグを持つコメントは保持します。 */
            Program()
            {
                for (const comment of context.sourceCode.getAllComments())
                {
                    if (comment.type !== 'Block' || comment.loc.start.line === comment.loc.end.line)
                    {
                        continue;
                    }
                    const jsdoc = comment.value.startsWith('*');
                    const lines = comment.value.split(/\r?\n/u).map(line => line.replace(/^\s*\*?\s?/u, '').trim()).filter(Boolean);
                    if (lines.length === 1)
                    {
                        context.report({ loc: comment.loc, messageId: 'compact',

                            fix: fixer => fixer.replaceTextRange(comment.range, `${jsdoc ? '/**' : '/*'} ${lines[0]} */`) });
                    }
                }
            }
        };
    }
};
