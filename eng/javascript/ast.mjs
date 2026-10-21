/** 関数の説明を置く宣言を特定し、無名コールバックを区別します。
 * @returns 説明対象の宣言、または対象外です。
 */
export function declaration(node)
{
    let target = node;
    if (node.type === 'FunctionExpression' || node.type === 'ArrowFunctionExpression' || node.type === 'ClassExpression')
    {
        const parent = node.parent;
        if (parent.type === 'VariableDeclarator')
        {
            target = parent.parent;
        }
        else if (parent.type === 'MethodDefinition' || (parent.type === 'Property' && parent.method))
        {
            target = parent;
        }
        else
        {
            return null;
        }
    }
    if (target.parent?.type === 'ExportNamedDeclaration' || target.parent?.type === 'ExportDefaultDeclaration')
    {
        target = target.parent;
    }
    return target;
}

/** 宣言に直接付いたJSDocだけを読みます。
 * @returns JSDocの本文、または未記載です。
 */
export function documentation(source, target)
{
    const comment = source.getCommentsBefore(target).at(-1);
    if (comment?.type !== 'Block' || !comment.value.startsWith('*') || (1) < (target.loc.start.line - comment.loc.end.line))
    {
        return null;
    }
    return comment.value.replace(/^\s*\*\s?/gmu, '').trim();
}

/** 文字列に日本語の説明が含まれるかを調べます。
 * @returns 日本語文字がある場合は真です。
 */
export function japanese(text)
{
    return /[\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Han}]/u.test(text);
}
