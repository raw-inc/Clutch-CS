/** JSONの型名へ対応するJSDocのスカラー名です。 */
const primitives = new Map([['string', 'string'], ['number', 'number'], ['boolean', 'boolean'], ['any', 'any'], ['Object', 'object'], ['object', 'object']]);

/** 入れ子のジェネリック型を実行せず、辞書と配列の構造だけを読みます。
 * @returns 表現可能な型、または未確定です。
 */
function annotation(text, depth = 0)
{
    if (16 < depth)
    {
        return null;
    }
    const value = text.trim();
    if (primitives.has(value))
    {
        return { type: primitives.get(value) };
    }
    if (value.endsWith('[]'))
    {
        const elementType = annotation(value.slice(0, -2), depth + 1);
        return elementType ? { type: 'array', elementType } : null;
    }
    const generic = /^(?:Record|Object\.?)\s*<\s*(string)\s*,\s*(.+)>$/u.exec(value);
    if (generic)
    {
        const valueType = annotation(generic[2], depth + 1);
        return valueType ? { type: 'object', keyType: { type: 'string' }, valueType } : null;
    }
    return null;
}

/** run直前のJSDocだけを参照し、他の関数やコメントの型を流用しません。
 * @returns 名前付き引数と返却値の明示型です。
 */
export function readAnnotations(code, comments, entry)
{
    const comment = comments.filter(item => item.type === 'Block' && item.end <= entry.start && item.value.startsWith('*')).at(-1);
    if (!comment || !/^(?:\s|export|default)*$/u.test(code.slice(comment.end, entry.start)))
    {
        return { arguments: {}, result: null };
    }
    const args = [];
    for (const match of comment.value.matchAll(/@param\s*\{([^{}]+)\}\s+([\w$]+)/gu))
    {
        const type = annotation(match[1]);
        if (type)
        {
            args.push([match[2], type]);
        }
    }
    const result = /@returns?\s*\{([^{}]+)\}/u.exec(comment.value);
    return { arguments: Object.fromEntries(args), result: result ? annotation(result[1]) : null };
}
