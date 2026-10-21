import stylelint from 'stylelint';

/** 通常の詳細度で解決できない宣言だけに、隣接する具体的な例外理由を要求します。
 * @returns Stylelintが呼び出す宣言検査です。
 */
function importantReason(enabled)
{
    return (root, result) =>
    {
        if (!stylelint.utils.validateOptions(result, 'quality/important-reason', { actual: enabled, possible: [true] }))
        {
            return;
        }
        root.walkDecls(declaration =>
        {
            const previous = declaration.prev();
            if (declaration.important && (previous?.type !== 'comment' || !/^例外理由:\s*\S.*[ぁ-んァ-ヶ一-龠]/u.test(previous.text.trim())))
            {
                stylelint.utils.report({ ruleName: 'quality/important-reason', result, node: declaration, message: '!importantの直前に「例外理由:」と具体的な日本語の理由を記述してください。' });
            }
        });
    };
}

/** 検査の無効化と解消条件のない保留を拒否し、短い説明を一行に揃えます。
 * @returns Stylelintが呼び出すコメント検査です。
 */
function comments(enabled)
{
    return (root, result) =>
    {
        if (!stylelint.utils.validateOptions(result, 'quality/comments', { actual: enabled, possible: [true] }))
        {
            return;
        }
        root.walkComments(comment =>
        {
            const text = comment.text.trim();
            let message;
            if (/\bstylelint-(?:disable|enable|config)\b/iu.test(text))
            {
                message = '検査のインライン無効化・設定変更は禁止です。適用範囲は構成ファイルで定義してください。';
            }
            else if (/\b(?:TODO|FIXME|FYI)\b/u.test(text) && (!/[ぁ-んァ-ヶ一-龠]/u.test(text) || !/(?:解消条件|対応条件):\s*\S/u.test(text)))
            {
                message = '保留コメントには日本語の理由と「解消条件:」を記述してください。';
            }
            else if (!text.includes('\n') && /[\r\n]/u.test(comment.toString()))
            {
                message = '本文が一行のコメントは、閉じ記号まで同じ行に収めてください。';
            }
            if (message)
            {
                stylelint.utils.report({ ruleName: 'quality/comments', result, node: comment, message });
            }
        });
    };
}

/** プロジェクトのパスや画面名に依存しない追加検査です。 */
export default [stylelint.createPlugin('quality/important-reason', importantReason), stylelint.createPlugin('quality/comments', comments)];
