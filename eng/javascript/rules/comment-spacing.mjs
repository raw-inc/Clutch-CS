/** 独立した説明コメントの前を一行空け、ブロック先頭では空けません。 */
export default {
    meta: {
        type: 'layout', fixable: 'whitespace', schema: [],
        messages: { spacing: 'コメント直前の空行は{{count}}行にしてください。', indent: 'JSDocの字下げを説明対象の宣言に揃えてください。' }
    },

    /** コメント群と行末の補足を区別して空行を検査します。
     * @returns ファイル全体のコメントを検査する処理です。
     */
    create(context)
    {
        const source = context.sourceCode;

        /** 行の先頭にあるコメントかを判定します。
         * @returns コメントより前が空白だけならtrueです。
         */
        function standalone(comment)
        {
            const start = source.text.lastIndexOf('\n', comment.range[0] - 1) + 1;
            return /^\s*$/u.test(source.text.slice(start, comment.range[0]));
        }

        /** 宣言の前に置いたJSDocの各行を宣言の字下げに揃えます。 */
        function inspectIndent(comment)
        {
            if (comment.type !== 'Block' || !comment.value.startsWith('*') || !standalone(comment))
            {
                return;
            }
            const next = source.getTokenAfter(comment);
            if (!next || next.loc.start.line <= comment.loc.end.line || ['}', ')', ']'].includes(next.value))
            {
                return;
            }
            const nextStart = source.text.lastIndexOf('\n', next.range[0] - 1) + 1;
            const indentation = source.text.slice(nextStart, next.range[0]);
            if (!/^[\t ]*$/u.test(indentation))
            {
                return;
            }
            const start = source.text.lastIndexOf('\n', comment.range[0] - 1) + 1;
            const original = source.text.slice(start, comment.range[1]);
            const newline = original.includes('\r\n') ? '\r\n' : '\n';
            const lines = source.getText(comment).split(/\r?\n/u);
            const formatted = indentation + lines.map((line, index) => index ? line.replace(/^[\t ]*\*/u, indentation + ' *') : line).join(newline);
            if (formatted !== original)
            {
                context.report({ loc: comment.loc, messageId: 'indent',

                    fix: fixer => fixer.replaceTextRange([start, comment.range[1]], formatted) });
            }
        }

        return {
            /** 各コメント群の先頭と、その直前の構文を確認します。 */
            Program()
            {
                for (const comment of source.getAllComments())
                {
                    inspectIndent(comment);
                    const previous = source.getTokenBefore(comment, { includeComments: true });
                    const jsdoc = comment.type === 'Block' && comment.value.startsWith('*');
                    if (!standalone(comment) && !(jsdoc && ['{', ',', ';', '}'].includes(previous?.value)))
                    {
                        continue;
                    }
                    let anchor = previous;
                    if (previous?.type === 'Line' || previous?.type === 'Block')
                    {
                        if (standalone(previous) && comment.loc.start.line === previous.loc.end.line + 1)
                        {
                            continue;
                        }
                        anchor = source.getTokenBefore(previous);
                        if (anchor?.loc.end.line !== previous.loc.start.line)
                        {
                            anchor = previous;
                        }
                    }
                    const start = previous?.range[1] ?? 0;
                    const gap = source.text.slice(start, comment.range[0]);
                    if (!/^\s*$/u.test(gap))
                    {
                        continue;
                    }
                    const count = !previous || anchor?.value === '{' ? 0 : 1;
                    const lines = previous ? count + 1 : 0;
                    if ((gap.match(/\n/gu) ?? []).length === lines)
                    {
                        continue;
                    }
                    const indentation = gap.match(/[^\S\r\n]*$/u)[0];
                    const newline = gap.includes('\r\n') ? '\r\n' : '\n';
                    context.report({ loc: comment.loc, messageId: 'spacing', data: { count },

                        fix: fixer => fixer.replaceTextRange([start, comment.range[0]], newline.repeat(lines) + indentation) });
                }
            }
        };
    }
};
