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
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>配布するコードを解析し、通常のスクリプトノードから実行します。</summary>
public sealed class ScriptSampleTests
{
    private static readonly string[] expectedLabels = ["news", "weather", "News", "お知らせ"];
    private static readonly string[] labelsWithInnerSpace = ["  daily news  "];

    /// <summary>C#サンプルの配列入出力を実ワーカーで確認します。</summary>
    /// <returns>サンプルの検証タスクです。</returns>
    [Fact]
    public async Task CSharpSampleNormalizesLabelsAsync() => await VerifyAsync("csharp", "normalize-labels.csx").ConfigureAwait(true);

    /// <summary>Pythonサンプルの型ヒントと実行結果を確認します。</summary>
    /// <returns>サンプルの検証タスクです。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonSampleNormalizesLabelsAsync() => await VerifyAsync("python", "normalize-labels.py").ConfigureAwait(true);

    /// <summary>JavaScriptサンプルのJSDocと実行結果を確認します。</summary>
    /// <returns>サンプルの検証タスクです。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task JavaScriptSampleNormalizesLabelsAsync() => await VerifyAsync("javascript", "normalize-labels.mjs").ConfigureAwait(true);

    /// <summary>実ファイルの解析結果を使ってノードを構成します。</summary>
    /// <returns>入出力の検証タスクです。</returns>
    private static async Task VerifyAsync(string language, string fileName, CancellationToken cancellationToken = default)
    {
        string code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ScriptingSamples", fileName), cancellationToken).ConfigureAwait(true);
        ScriptAnalysisRequest request = language switch
        {
            "python" => new(language, code, PythonPath: Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON")),
            "javascript" => new(language, code, NodePath: Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")),
            _ => new(language, code)
        };
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        Assert.NotNull(analysis.Contract);
        Assert.Equal("values", Assert.Single(analysis.Contract.Arguments).Key);
        Assert.Equal("array", analysis.Contract.Arguments["values"].Type);
        Assert.Equal("string", analysis.Contract.Arguments["values"].ElementType!.Type);
        Assert.Equal("array", analysis.Contract.Result.Type);
        Assert.Equal("string", analysis.Contract.Result.ElementType!.Type);
        JsonObject settings = new() { ["code"] = code, ["analysis"] = JsonSerializer.SerializeToNode(analysis, ContractJson.Options) };
        if (request.PythonPath is not null)
        {
            settings["pythonPath"] = request.PythonPath;
        }
        if (request.NodePath is not null)
        {
            settings["nodePath"] = request.NodePath;
        }
        string host = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            host += ".exe";
        }
        IActionNode node = (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, host), [])).Single(item => string.Equals(item.Descriptor.Type, "script." + language, StringComparison.Ordinal));
        await CheckResultsAsync(node, settings, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>入力方式、空配列、空白、重複、型不一致を確認します。</summary>
    /// <returns>実プロセスの結果を照合するタスクです。</returns>
    private static async Task CheckResultsAsync(IActionNode node, JsonObject settings, CancellationToken cancellationToken)
    {
        foreach (string inputMode in new[] { "individual", "object" })
        {
            settings["inputMode"] = inputMode;
            string[] values = ["  news ", "", "weather", "news", "News", " お知らせ ", "\t"];
            NodeResult result = await ExecuteAsync(node, settings, values, cancellationToken).ConfigureAwait(true);
            Assert.Null(result.ReasonCode);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(expectedLabels, result.Outputs["value"].Json.EnumerateArray().Select(item => item.GetString()));
            foreach (string[] empty in new[] { Array.Empty<string>(), new[] { " ", "\t", "\r\n" } })
            {
                NodeResult cleared = await ExecuteAsync(node, settings, empty, cancellationToken).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, cleared.Outcome);
                Assert.Empty(cleared.Outputs["value"].Json.EnumerateArray());
            }
            NodeResult spaced = await ExecuteAsync(node, settings, labelsWithInnerSpace, cancellationToken).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, spaced.Outcome);
            Assert.Equal("daily news", Assert.Single(spaced.Outputs["value"].Json.EnumerateArray()).GetString());
            foreach (string invalid in new[] { "[1]", "[null]", "null", "\"news\"" })
            {
                NodeResult rejected = await ExecuteAsync(node, settings, JsonSerializer.Deserialize<JsonElement>(invalid), cancellationToken).ConfigureAwait(true);
                Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
            }
        }
    }

    /// <summary>指定した方式で入力し、ノードを一回実行します。</summary>
    /// <returns>通常の実行結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(IActionNode node, JsonObject settings, object values, CancellationToken cancellationToken)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        string inputMode = settings["inputMode"]!.GetValue<string>();
        string individual = "individual";
        if (string.Equals(inputMode, individual, StringComparison.Ordinal))
        {
            inputs["$arguments.values"] = NodeValue.FromObject(values);
        }
        else
        {
            inputs["arguments"] = NodeValue.FromObject(new { values });
        }
        return await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true);
    }
}
