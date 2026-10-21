import { declaration, documentation, japanese } from '../ast.mjs';

/** 非同期処理を名前から識別し、変更できない外部契約には説明を求めます。 */
export default {
    meta: { type: 'suggestion', schema: [], messages: { name: '非同期関数にはAsync接尾辞を付けてください。固定APIは日本語の@contractで理由を説明してください。' } },

    /** このファイルに適用する構文検査を構成します。
     * @returns 構文ごとの検査処理です。 */
    create(context)
    {
        return {
            /** 非同期の名前付き関数を検査します。 */
            ':function'(node)
            {
                if (!node.async)
                {
                    return;
                }
                const target = declaration(node);
                const name = node.id?.name ?? (node.parent.type === 'VariableDeclarator' ? node.parent.id.name : node.parent.key?.name);
                if (!target || !name || name.endsWith('Async'))
                {
                    return;
                }
                const contract = documentation(context.sourceCode, target)?.match(/@contract\s+([^\n]+)/u)?.[1];
                if (!contract || !japanese(contract))
                {
                    context.report({ node, messageId: 'name' });
                }
            }
        };
    }
};
