/** JSONで表せる共通の数値型です。 */
const numeric = new Set(['number', 'integer', 'int64', 'decimal', 'byte']);

/** 推定で特別に扱う構文・演算子・文字列型の名前です。 */
const syntax = Object.freeze({ property: 'Property', initializer: 'init', addition: '+', logicalNot: '!', typeOf: 'typeof', text: 'string' });

/** nullと型の合流だけを許し、異なる返却経路を勝手にanyへ広げません。
 * @returns 共通型、または明示選択が必要なnullです。
 */
export function mergeTypes(values)
{
    if (values.length === 0 || values.some(value => value === null))
    {
        return null;
    }
    const concrete = values.filter(value => !(value.type === 'any' && value.optional));
    if (concrete.length === 0)
    {
        return { type: 'any', optional: true };
    }
    const first = concrete[0];
    if (concrete.some(value => JSON.stringify(value) !== JSON.stringify(first)))
    {
        return null;
    }
    return values.length === concrete.length ? first : { ...first, optional: true };
}

/** 実行しない構文木から、リテラルのJSON型だけを取得します。
 * @returns JSONで表現可能な型、または未確定です。
 */
function literalType(value)
{
    if (value === null)
    {
        return { type: 'any', optional: true };
    }
    const type = typeof value;
    return ['string', 'boolean', 'number'].includes(type) ? { type } : null;
}

/** オブジェクトの動的キー・spreadを不確実な構造のまま確定させません。
 * @returns 全プロパティの型を確認できたオブジェクトです。
 */
function objectType(node, bindings)
{
    const properties = [];
    for (const property of node.properties)
    {
        if (property.type !== syntax.property || property.computed || property.method || property.kind !== syntax.initializer)
        {
            return null;
        }
        const name = property.key.name ?? property.key.value;
        const value = inferType(property.value, bindings);
        if (typeof name !== syntax.text || value === null)
        {
            return null;
        }
        properties.push([name, value]);
    }
    return { type: 'object', properties: Object.fromEntries(properties) };
}

/** 入力型を確認できた演算だけを数値・文字列として推定します。
 * @returns 演算結果の静的な型です。
 */
function binaryType(node, bindings)
{
    if (['===', '!==', '==', '!=', '<', '<=', '>', '>=', 'in', 'instanceof'].includes(node.operator))
    {
        return { type: 'boolean' };
    }
    const left = inferType(node.left, bindings), right = inferType(node.right, bindings);
    if (left === null || right === null)
    {
        return null;
    }
    if (node.operator === syntax.addition && left.type === syntax.text && right.type === syntax.text)
    {
        return { type: 'string' };
    }
    if (numeric.has(left.type) && numeric.has(right.type))
    {
        return { type: 'number' };
    }
    return null;
}

/** npmや利用者関数を呼ばず、既知の式だけをWorkflowEditorの型へ変換します。
 * @returns 推定型、または手動の型選択が必要なnullです。
 */
export function inferType(node, bindings)
{
    if (!node)
    {
        return null;
    }
    switch (node.type)
    {
        case 'Literal': return literalType(node.value);
        case 'Identifier': return bindings.get(node.name) ?? null;
        case 'TemplateLiteral': return { type: 'string' };
        case 'ObjectExpression': return objectType(node, bindings);
        case 'ArrayExpression':
        {
            const elementType = node.elements.length === 0 ? { type: 'any' } : mergeTypes(node.elements.map(element => inferType(element, bindings)));
            return elementType === null ? null : { type: 'array', elementType };
        }
        case 'BinaryExpression': return binaryType(node, bindings);
        case 'ConditionalExpression': return mergeTypes([inferType(node.consequent, bindings), inferType(node.alternate, bindings)]);
        case 'LogicalExpression': return mergeTypes([inferType(node.left, bindings), inferType(node.right, bindings)]);
        case 'UnaryExpression':
        {
            if (node.operator === syntax.logicalNot)
            {
                return { type: 'boolean' };
            }
            if (node.operator === syntax.typeOf)
            {
                return { type: 'string' };
            }
            const operand = inferType(node.argument, bindings);
            return operand && numeric.has(operand.type) ? { type: 'number' } : null;
        }
        default: return null;
    }
}

/** 入れ子の関数・クラスのreturnや代入を、入口関数のものとして扱いません。
 * @returns 入口関数内で調べる構文ノードです。
 */
export function* bodyNodes(node)
{
    if (!node || ['FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression', 'ClassDeclaration', 'ClassExpression'].includes(node.type))
    {
        return;
    }
    yield node;
    for (const value of Object.values(node))
    {
        if (Array.isArray(value))
        {
            for (const child of value)
            {
                if (child?.type)
                {
                    yield* bodyNodes(child);
                }
            }
        }
        else
        {
            if (value?.type)
            {
                yield* bodyNodes(value);
            }
        }
    }
}
