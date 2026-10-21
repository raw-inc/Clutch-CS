using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>各スクリプト子プロセスの例外が、元のノードのログへ届くことを検証します。</summary>
public sealed class ScriptExceptionLoggingTests
{
    /// <summary>C#の反射呼出し内の例外と、元のRunメソッドのスタックを保持します。</summary>
    /// <returns>実子プロセスの診断です。</returns>
    [Fact]
    public Task CSharpFailureHasOriginalStackAsync()
        => VerifyAsync("csharp", "public static int Run(int value) => 42 / value;", "DivideByZeroException", "WorkflowScript.Run");

    /// <summary>Pythonのスクリプト内行番号と例外型を保持します。</summary>
    /// <returns>実Python子プロセスの診断です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public Task PythonFailureHasOriginalStackAsync()
        => VerifyAsync("python", "def run(value: int) -> int:\n    return 42 // value", "ZeroDivisionError", "script.py:2");

    /// <summary>JavaScriptのユーザー例外を入力や全コードの収集なしで記録します。</summary>
    /// <returns>実Node.js子プロセスの診断です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public Task JavaScriptFailureHasOriginalStackAsync()
        => VerifyAsync("javascript", "export function run(value) { throw new Error('script operation failed'); }", "Error", "script.mjs");

    /// <summary>小さい出力量制限でも、診断の追加で確認済み失敗を結果不明へ変えません。</summary>
    /// <returns>三言語の固定失敗結果と省略表示です。</returns>
    [Fact]
    public async Task SmallOutputLimitKeepsConfirmedFailureAsync()
    {
        await VerifyAsync("csharp", "public static int Run(int value) => 42 / value;", string.Empty, string.Empty, 1024).ConfigureAwait(true);
        string? python = Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON");
        string? node = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE");
        if (python is not null)
        {
            await VerifyAsync("python", "def run(value: int) -> int:\n    return 42 // value", string.Empty, string.Empty, 1024).ConfigureAwait(true);
        }
        if (node is not null)
        {
            await VerifyAsync("javascript", "export function run(value) { throw new Error('script operation failed'); }", string.Empty, string.Empty, 1024).ConfigureAwait(true);
        }
    }

    /// <summary>通常の解析・実行経路にだけ記録先を追加します。</summary>
    /// <returns>失敗結果を維持し、例外の元位置を取得する確認です。</returns>
    private static async Task VerifyAsync(string language, string code, string exceptionType, string stack, int maximumBytes = 1048576, CancellationToken cancellationToken = default)
    {
        string? python = null;
        string? nodePath = null;
        string pythonLanguage = "python";
        string javascriptLanguage = "javascript";
        if (string.Equals(language, pythonLanguage, StringComparison.Ordinal))
        {
            python = Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON");
        }
        if (string.Equals(language, javascriptLanguage, StringComparison.Ordinal))
        {
            nodePath = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE");
        }
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(new(language, code, PythonPath: python, NodePath: nodePath), cancellationToken).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        JsonObject settings = new() { ["code"] = code, ["analysis"] = JsonSerializer.SerializeToNode(analysis, ContractJson.Options), ["maxBytes"] = maximumBytes };
        if (python is not null)
        {
            settings["pythonPath"] = python;
        }
        if (nodePath is not null)
        {
            settings["nodePath"] = nodePath;
        }
        string host = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            host += ".exe";
        }
        IActionNode node = (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, host), []))
            .Single(item => string.Equals(item.Descriptor.Type, "script." + language, StringComparison.Ordinal));
        RecordingWorkflowLog log = new();
        NodeExecutionContext context = NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { value = 0 }) }) with { Log = log };
        NodeResult result = await node.ExecuteAsync(context, cancellationToken).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Failed, JsonSerializer.Serialize(result));
        Assert.Equal("script.executionFailed", result.ReasonCode);
        Contracts.Observability.Logging.WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("node.exception", entry.Message);
        Assert.Equal(context.NodeId, entry.NodeId);
        Assert.Equal(context.CausationId, entry.CausationId);
        Assert.Contains(exceptionType, entry.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(stack, entry.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("arguments", entry.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(result.Outputs);
    }
}
