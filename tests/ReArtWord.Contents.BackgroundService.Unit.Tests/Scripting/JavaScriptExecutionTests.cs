using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>実Node.jsプロセスの型付き実行・解析の非実行性・失敗境界を確認します。</summary>
public sealed class JavaScriptExecutionTests
{
    /// <summary>JSDocの辞書型を推論し、Objectとして通常のJavaScriptコードへ渡します。</summary>
    /// <returns>配列値・特殊キーを含む辞書の実Node.js往復です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task DictionaryAnnotationsAndObjectOperationsWorkAsync()
    {
        string code = "/** @param {Record<string, number[]>} values\n * @returns {Record<string, number[]>} */\nexport function run(values) { return Object.fromEntries(Object.entries(values).map(([key, value]) => [key, [...value, 3]])); }";
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(new("javascript", code, NodePath: RuntimePath())).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal("string", analysis.Contract!.Arguments["values"].KeyType!.Type);
        Assert.Equal("array", analysis.Contract.Result.ValueType!.Type);
        NodeResult result = await ExecuteAsync(code, new { values = new Dictionary<string, int[]>(StringComparer.Ordinal) { ["__proto__"] = [1, 2], ["01"] = [] } }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.Outputs["value"].Json.GetProperty("__proto__").GetArrayLength());
        Assert.Equal("string", result.Outputs["value"].DataType!.KeyType!.Type);
        NodeResult defaults = await ExecuteAsync("/** @param {Object.<string, number>} values */\nexport function run(values = {}) { return values; }", new { values = new { A = 7 } }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, defaults.Outcome);
        Assert.Equal(7, defaults.Outputs["value"].Json.GetProperty("A").GetInt32());
    }

    /// <summary>名前付きJSONの順序に依存せず、既定値と非同期完了を扱います。</summary>
    /// <returns>実子プロセスによる型付き結果です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task NamedArgumentsDefaultsAndAsyncCompletionWorkAsync()
    {
        NodeResult result = await ExecuteAsync("export async function run(left = 'left', right = 'right') { console.log('not protocol'); await Promise.resolve(); return left + ':' + right; }",
            new { right = "tail" }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("left:tail", result.Outputs["value"].GetString());
        NodeResult ordered = await ExecuteAsync("export function run(left = 0, right = 0) { return left - right; }", new { right = 3, left = 8 }).ConfigureAwait(true);
        Assert.Equal(5, ordered.Outputs["value"].Json.GetInt32());
    }

    /// <summary>構文解析中は、import先もトップレベルの利用者コードも起動しません。</summary>
    /// <returns>静的解析と実行時のエラー分類です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task AnalysisDoesNotExecuteImportsOrUserStatementsAsync()
    {
        ScriptAnalysisRequest request = new("javascript", "import missing from 'contents-definitely-not-installed'; throw new Error('secret'); export function run() { return 1; }", NodePath: RuntimePath());
        ScriptAnalysis report = await ScriptAnalysisService.AnalyzeAsync(request).ConfigureAwait(true);
        Assert.Empty(report.Diagnostics);
        Assert.NotNull(report.Contract);
        NodeResult result = await ExecuteAsync(request.Code, new { }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>動的な返却式の型は明示選択し、その型で実行結果を検査します。</summary>
    /// <returns>手動契約と、未登録パッケージを必要としない組込みimportの確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task ManualTypesAndNodeBuiltinImportsWorkAsync()
    {
        string code = "import { basename } from 'node:path'; export function run(value) { return basename(value); }";
        JsonElement manual = JsonSerializer.SerializeToElement(new { arguments = new { value = new { type = "string" } }, result = new { type = "string" } });
        NodeResult result = await ExecuteAsync(code, new { value = "folder/file.txt" }, manual).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("file.txt", result.Outputs["value"].GetString());
    }

    /// <summary>JSONが省略・null化してしまうJavaScript値を成功として返しません。</summary>
    /// <returns>NaN・undefined・BigInt・循環の実行境界です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task InvalidJsonResultsFailWithoutLeakingExceptionsAsync()
    {
        JsonElement manual = JsonSerializer.SerializeToElement(new { arguments = new { }, result = new { type = "any" } });
        foreach (string body in new[] { "return NaN;", "return undefined;", "return 1n;", "return { value: undefined };", "const value = {}; value.self = value; return value;" })
        {
            NodeResult result = await ExecuteAsync("export function run() { " + body + " }", new { }, manual).ConfigureAwait(true);
            Assert.Equal(EOutcome.Failed, result.Outcome);
        }
    }

    /// <summary>JavaScriptで正確に表せないInt64入力は、ユーザーコード開始前に拒否します。</summary>
    /// <returns>入力を黙って丸めない未実行結果です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task UnsafeIntegerInputIsNotExecutedAsync()
    {
        JsonElement manual = JsonSerializer.SerializeToElement(new { arguments = new { value = new { type = "integer" } }, result = new { type = "integer" } });
        NodeResult result = await ExecuteAsync("export function run(value) { return value; }", new { value = 9007199254740993L }, manual).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
    }

    /// <summary>無限待機と過大な返却値は、完了を確認できない一回として扱います。</summary>
    /// <returns>プロセス期限・出力量制限と停止の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task TimeoutAndOutputLimitDoNotSucceedAsync()
    {
        NodeResult timeout = await ExecuteAsync("export async function run() { await new Promise(resolve => setTimeout(resolve, 60000)); return 1; }", new { }, timeoutMs: 300).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, timeout.Outcome);
        JsonElement manual = JsonSerializer.SerializeToElement(new { arguments = new { }, result = new { type = "string" } });
        NodeResult large = await ExecuteAsync("export function run() { return 'x'.repeat(100000); }", new { }, manual, maximumBytes: 2048).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, large.Outcome);
    }

    /// <summary>解析先のNode.jsを明示し、保存した同じ内容を通常ノードで実行します。</summary>
    /// <returns>実プロセスで観測した一回の結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(string code, object arguments, JsonElement manual = default, int timeoutMs = 30000, int maximumBytes = 1048576, CancellationToken cancellationToken = default)
    {
        ScriptAnalysisRequest request = new("javascript", code, ManualContract: manual, NodePath: RuntimePath());
        ScriptAnalysis report = await ScriptAnalysisService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        Assert.Empty(report.Diagnostics);
        Assert.NotNull(report.Contract);
        System.Text.Json.Nodes.JsonObject settings = JsonSerializer.SerializeToNode(new { code, nodePath = request.NodePath,
            analysis = report, timeoutMs, maxBytes = maximumBytes }, ContractJson.Options)!.AsObject();
        if (manual.ValueKind != JsonValueKind.Undefined)
        {
            settings["manualContract"] = JsonSerializer.SerializeToNode(manual, ContractJson.Options);
        }
        IActionNode node = (IActionNode)ScriptingNodeRegistration.Create(new("unused", [])).Single(node => node.Descriptor.Type is "script.javascript");
        return await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = NodeValue.FromObject(arguments) }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>試験環境で明示したNode.jsだけを使用します。</summary>
    /// <returns>実行ファイルの絶対パスです。</returns>
    private static string RuntimePath() => Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")!;
}
