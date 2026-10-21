import { readFile, writeFile } from 'node:fs/promises';
import { isBuiltin, registerHooks } from 'node:module';
import { join, relative, isAbsolute, sep } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { analyzeCode } from './javascript-contract.mjs';

/** 実行ホストの返却値だけを、利用者の標準出力と分離して書きます。 */
const protocol = process.stdout.write.bind(process.stdout);

/** C#側が明示する静的解析モードです。 */
const analysisMode = 'analyze';

/** 通常のnpm解決規則を使い、管理外のnode_modulesへの暗黙の代替を拒否します。 */
function bindPackages(directory, scriptPath)
{
    const anchor = directory ? pathToFileURL(join(directory, 'entry.mjs')).href : null;
    const scriptUrl = pathToFileURL(scriptPath).href;
    registerHooks({
        /** パッケージのexports・推移依存をNode.js自身のresolverへ委ねます。
         * @returns 今回の固定構成内で解決したモジュールです。
         */
        resolve(specifier, context, nextResolve)
        {
            const bare = !/^(?:[a-z][a-z\d+.-]*:|[./])/iu.test(specifier) && !isBuiltin(specifier);
            if (bare && !directory)
            {
                throw new Error('script.javascriptPackageUnavailable');
            }
            const resolution = nextResolve(specifier, bare && context.parentURL === scriptUrl ? { ...context, parentURL: anchor } : context);
            if (bare && !isBuiltin(resolution.url))
            {
                const path = relative(join(directory, 'node_modules'), fileURLToPath(resolution.url));
                const parent = '..';
                if (path === parent || path.startsWith(`..${sep}`) || isAbsolute(path))
                {
                    throw new Error('script.javascriptPackageUnavailable');
                }
            }
            return resolution;
        }
    });
}

/** JSON.stringifyが黙って省略・null化・丸めを行う返却値を拒否します。 */
function validateResult(value, ancestors = new Set())
{
    if (value === null || typeof value === 'string' || typeof value === 'boolean')
    {
        return;
    }
    if (typeof value === 'number')
    {
        if (!Number.isFinite(value) || Number.isInteger(value) && !Number.isSafeInteger(value))
        {
            throw new Error('script.javascriptUnsafeNumber');
        }
        return;
    }
    if (typeof value !== 'object' || ancestors.has(value) || (64) <= (ancestors.size))
    {
        throw new Error('script.javascriptResultInvalid');
    }
    if (!Array.isArray(value) && ![Object.prototype, null].includes(Object.getPrototypeOf(value)))
    {
        throw new Error('script.javascriptResultInvalid');
    }
    ancestors.add(value);
    for (const item of Array.isArray(value) ? value : Object.values(value))
    {
        validateResult(item, ancestors);
    }
    ancestors.delete(value);
}

/** 一回分のES Moduleを読み込み、Promiseの完了まで待ちます。
 * @returns JSONへそのまま渡せる利用者の返却値です。
 */
async function executeAsync(request, inputPath)
{
    process.stdout.write = process.stderr.write.bind(process.stderr);
    const scriptPath = join(fileURLToPath(new URL('.', pathToFileURL(inputPath))), 'script.mjs');
    await writeFile(scriptPath, request.code, 'utf8');
    bindPackages(request.packageDirectory, scriptPath);
    const module = await import(pathToFileURL(scriptPath).href);
    const defaults = new Set(analyzeCode({ code: request.code }).defaultArguments);
    const argumentsList = [];
    for (const name of request.argumentNames)
    {
        const supplied = Object.hasOwn(request.arguments, name) ? request.arguments[name] : undefined;
        if (name === request.variadicArgument)
        {
            argumentsList.push(...supplied);
        }
        else
        {
            argumentsList.push(supplied === null && defaults.has(name) ? undefined : supplied);
        }
    }
    const value = await module.run(...argumentsList);
    validateResult(value);
    return value;
}

/** ローカル変数やソース全文を収集せず、親プロセスのログ保護境界へ診断を渡します。
 * @returns 容量を制限した例外診断です。
 */
function exceptionDiagnostic(error, depth = 0)
{
    let truncated = false;

    /** 長い自由文は途中で切らず、全体を省略します。
     * @returns 上限内の自由文または省略表示です。
     */
    const text = (value, maximum) =>
    {
        if (typeof value !== 'string')
        {
            return null;
        }
        if ((maximum) < (value.length))
        {
            truncated = true;
            return '[omitted: length limit]';
        }
        return value;
    };

    try
    {
        const type = text(error?.name, 256) ?? typeof error;
        const message = text(error?.message ?? error, 2048);
        const stackTrace = text(error?.stack, 6144);
        const innerExceptions = [];
        if (error?.cause)
        {
            if ((depth) < (2))
            {
                innerExceptions.push(exceptionDiagnostic(error.cause, depth + 1));
            }
            else
            {
                truncated = true;
            }
        }
        return { type, message, stackTrace, hResult: 0, innerExceptions, truncated };
    }
    catch
    {
        return { type: 'UnknownError', message: null, stackTrace: null, hResult: 0, innerExceptions: [], truncated: true };
    }
}

/** 自由記述コードの最上位境界で、失敗と診断を親プロセスへ返します。
 * @returns 一回の解析または実行結果の送出です。
 */
async function mainAsync()
{
    let response;
    let maximumDiagnosticBytes = 1048576;
    const mode = process.argv[2];
    try
    {
        if (Number(process.versions.node.split('.')[0]) < 24)
        {
            throw new Error('script.javascriptRuntimeRequired');
        }
        const request = JSON.parse(await readFile(process.argv[3], 'utf8'));
        maximumDiagnosticBytes = Math.max(1024, request.maximumDiagnosticBytes ?? maximumDiagnosticBytes);
        response = mode === analysisMode ? analyzeCode(request) : { completed: true, value: await executeAsync(request, process.argv[3]) };
    }
    catch (error)
    {
        response = mode === analysisMode ? { contract: null, diagnostics: ['script.javascriptAnalysisUnavailable'] } : { completed: false, error: 'script.executionFailed', exception: exceptionDiagnostic(error) };
        if ((maximumDiagnosticBytes) < (Buffer.byteLength(JSON.stringify(response), 'utf8')))
        {
            response.exception = { type: 'ScriptError', message: '[diagnostic omitted: output limit]', stackTrace: null, hResult: 0, innerExceptions: [], truncated: true };
        }
    }
    await new Promise(resolve =>
    {
        protocol(JSON.stringify(response), resolve);
    });
    process.exit(0);
}

await mainAsync();
