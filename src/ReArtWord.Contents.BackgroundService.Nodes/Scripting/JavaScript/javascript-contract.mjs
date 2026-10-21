import { parse } from './vendor/acorn.mjs';
import { inferType, mergeTypes, bodyNodes } from './javascript-types.mjs';
import { readAnnotations } from './javascript-annotations.mjs';

/** 入口契約として確認する構文ノードと宣言形式です。 */
const syntax = Object.freeze({ exported: 'ExportNamedDeclaration', defaultValue: 'AssignmentPattern', rest: 'RestElement', identifier: 'Identifier', declaration: 'VariableDeclaration', constant: 'const', assignment: 'AssignmentExpression', update: 'UpdateExpression', array: 'array' });

/** exportされたrunだけを公開入口として選びます。
 * @returns 名前付き引数を持つ一つの関数、またはnullです。
 */
function entryFunction(program)
{
    const entries = program.body.map(node => node.type === syntax.exported ? node.declaration : node)
        .filter(node => node?.type === 'FunctionDeclaration' && node.id?.name === 'run');
    const exported = program.body.some(node => node.type === syntax.exported && !node.source
        && (node.declaration === entries[0] || node.specifiers?.some(specifier => specifier.local.name === 'run' && specifier.exported.name === 'run')));
    if (entries.length !== 1 || !exported || entries[0].generator)
    {
        return null;
    }
    return entries[0];
}

/** 未確定の引数だけに明示型を採用し、解析中に既定値式を実行しません。
 * @returns 名前を保持した型候補と診断です。
 */
function argumentTypes(entry, manual, diagnostics, declared)
{
    const argumentsByName = new Map();
    for (const parameter of entry.params)
    {
        const identifier = parameter.type === syntax.defaultValue ? parameter.left : parameter.type === syntax.rest ? parameter.argument : parameter;
        if (identifier.type !== syntax.identifier)
        {
            diagnostics.push('script.javascriptNamedArgumentsRequired');
            continue;
        }
        let type = Object.hasOwn(declared, identifier.name) ? declared[identifier.name] : parameter.type === syntax.defaultValue ? inferType(parameter.right, new Map()) : null;
        if (!type && Object.hasOwn(manual.arguments ?? {}, identifier.name))
        {
            type = manual.arguments[identifier.name];
        }
        if (parameter.type === syntax.rest)
        {
            type = { ...(type?.type === syntax.array ? type : { type: 'array', elementType: type ?? { type: 'any' } }), optional: true };
        }
        if (type && parameter.type === syntax.defaultValue)
        {
            type = { ...type, optional: true };
        }
        argumentsByName.set(identifier.name, type);
    }
    return argumentsByName;
}

/** 未推定を確定型の根拠にせず、公開するときだけanyへ変換します。
 * @returns 省略可能性を保つ公開引数です。
 */
function contractArguments(entry, argumentsByName)
{
    return Object.fromEntries([...argumentsByName].map(([name, type]) =>
    {
        const optional = entry.params.some(parameter => parameter.type === syntax.defaultValue && parameter.left.name === name);
        return [name, type ?? (optional ? { type: 'any', optional: true } : { type: 'any' })];
    }));
}

/** 静的な既知値を使い、書換えられる識別子を推定の根拠から外します。
 * @returns 返却型、または不明です。
 */
function returnType(entry, argumentsByName)
{
    const nodes = [...bodyNodes(entry.body)];
    const bindings = new Map(argumentsByName);
    for (const node of entry.body.body)
    {
        if (node.type === syntax.declaration && node.kind === syntax.constant)
        {
            for (const declaration of node.declarations)
            {
                if (declaration.id.type === syntax.identifier)
                {
                    bindings.set(declaration.id.name, inferType(declaration.init, bindings));
                }
            }
        }
    }
    for (const node of nodes)
    {
        const written = node.type === syntax.assignment ? node.left : node.type === syntax.update ? node.argument : null;
        if (written?.type === syntax.identifier)
        {
            bindings.set(written.name, null);
        }
    }
    return mergeTypes(nodes.filter(node => node.type === 'ReturnStatement').map(node => inferType(node.argument, bindings)));
}

/** import・関数・型推定対象の式を実行せず、構文と入出力だけを確認します。
 * @returns WorkflowEditorの型契約・未確定候補・構文診断です。
 */
export function analyzeCode(request)
{
    let program;
    const comments = [];
    try
    {
        program = parse(request.code, { ecmaVersion: 'latest', sourceType: 'module', locations: true, onComment: comments });
    }
    catch (error)
    {
        return { contract: null, diagnostics: [`JavaScript syntax error: ${error.message}`] };
    }
    if (request.syntaxOnly)
    {
        return { contract: null, diagnostics: [] };
    }
    const entry = entryFunction(program);
    if (!entry)
    {
        return { contract: null, diagnostics: ['script.javascriptEntryRequired'] };
    }
    const manual = request.manualContract ?? {}, diagnostics = [];
    const declared = readAnnotations(request.code, comments, entry);
    const argumentsByName = argumentTypes(entry, manual, diagnostics, declared.arguments);
    const result = declared.result ?? returnType(entry, argumentsByName) ?? manual.result ?? { type: 'any' };
    const candidate = { arguments: contractArguments(entry, argumentsByName), result };
    const variadic = entry.params.find(parameter => parameter.type === syntax.rest);
    if (variadic)
    {
        candidate.variadicArgument = variadic.argument.name;
    }
    const defaultArguments = entry.params.filter(parameter => parameter.type === syntax.defaultValue).map(parameter => parameter.left.name);
    return { contract: diagnostics.length === 0 ? candidate : null, suggestedContract: candidate, diagnostics, defaultArguments };
}
