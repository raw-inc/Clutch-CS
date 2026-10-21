using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>C#のバイト列を実workerから受け取り、文字列・null・JSON内の値と区別します。</summary>
public sealed class CSharpBinaryTests
{
    /// <summary>同期・非同期・object返却の実バイト列を、Base64文字列として公開しません。</summary>
    /// <returns>解析型と実workerのバイナリ返却です。</returns>
    [Theory]
    [InlineData("public static byte[] Run() => new byte[] { 0, 1, 255 };", "binary")]
    [InlineData("public static Task<byte[]> Run() => Task.FromResult(new byte[] { 0, 1, 255 });", "binary")]
    [InlineData("public static object Run() => new byte[] { 0, 1, 255 };", "any")]
    [InlineData("using Bytes = System.Byte[]; public static Bytes Run() => new byte[] { 0, 1, 255 };", "binary")]
    public async Task ByteResultsPreserveBinaryKindAsync(string code, string expectedType)
    {
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(expectedType, analysis.Contract!.Result.Type);
        Assert.Equal(1, analysis.ValueProtocolVersion);
        NodeResult result = await ExecuteAsync(Settings(code));
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        NodeValue value = result.Outputs["value"];
        Assert.Equal(EValueKind.Binary, value.Kind);
        Assert.Equal(new byte[] { 0, 1, 255 }, value.Binary.ToArray());
        Assert.Null(value.GetString());
    }

    /// <summary>空のバイト列とnullは別の値として後続へ渡します。</summary>
    /// <returns>バイナリ長0とJSON nullの区別です。</returns>
    [Fact]
    public async Task EmptyAndNullableByteResultsRemainDistinctAsync()
    {
        NodeResult empty = await ExecuteAsync(Settings("public static byte[] Run() => Array.Empty<byte>();"));
        Assert.Equal(EOutcome.Succeeded, empty.Outcome);
        Assert.Equal(EValueKind.Binary, empty.Outputs["value"].Kind);
        Assert.Empty(empty.Outputs["value"].Binary.ToArray());
        NodeResult absent = await ExecuteAsync(Settings("public static byte[]? Run() => null;"));
        Assert.Equal(EOutcome.Succeeded, absent.Outcome);
        Assert.Equal(EValueKind.Json, absent.Outputs["value"].Kind);
        Assert.Equal(JsonValueKind.Null, absent.Outputs["value"].Json.ValueKind);
    }

    /// <summary>有効なBase64に見える通常の文字列をバイナリへ推測変換しません。</summary>
    /// <returns>既存文字列返却の非回帰です。</returns>
    [Fact]
    public async Task NormalStringAndNestedJsonBytesKeepTheirJsonRepresentationAsync()
    {
        NodeResult text = await ExecuteAsync(Settings("public static string Run() => \"AAH/\";"));
        Assert.Equal(EOutcome.Succeeded, text.Outcome);
        Assert.Equal(EValueKind.Json, text.Outputs["value"].Kind);
        Assert.Equal("AAH/", text.Outputs["value"].GetString());
        NodeResult nested = await ExecuteAsync(Settings("public static byte[][] Run() => new[] { new byte[] { 0, 1, 255 } };"));
        Assert.Equal(EOutcome.Succeeded, nested.Outcome);
        Assert.Equal("AAH/", nested.Outputs["value"].Json[0].GetString());
    }

    /// <summary>バイナリ出力を次のC# byte[]個別入力へそのまま接続できます。</summary>
    /// <returns>入力ポートと実行時の往復確認です。</returns>
    [Fact]
    public async Task BinaryInputRoundTripsAndBulkModeStillAcceptsBase64Async()
    {
        string code = "public static byte[] Run(byte[] data) => data.Reverse().ToArray();";
        JsonObject settings = Settings(code);
        settings["inputMode"] = "individual";
        ScriptAnalysis report = CSharpScriptCompiler.Analyze(code);
        Assert.Equal("binary", report.Contract!.Arguments["data"].Type);
        NodeResult individual = await ExecuteAsync(settings, new Dictionary<string, NodeValue>
        {
            [ScriptInputPorts.Name("data")] = NodeValue.FromBytes(new byte[] { 0, 1, 255 }),
        });
        Assert.True(individual.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(individual));
        Assert.Equal(new byte[] { 255, 1, 0 }, individual.Outputs["value"].Binary.ToArray());
        settings["inputMode"] = "object";
        NodeResult bulk = await ExecuteAsync(settings, new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { data = "AAH/" }) });
        Assert.Equal(EOutcome.Succeeded, bulk.Outcome);
        Assert.Equal(individual.Outputs["value"].Binary.ToArray(), bulk.Outputs["value"].Binary.ToArray());
        NodeResult invalid = await ExecuteAsync(settings, new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { data = "not base64" }) });
        Assert.Equal(EOutcome.NotExecuted, invalid.Outcome);
    }

    /// <summary>文字列として保存された旧解析でもコード変更なしに実行でき、新結果はバイナリになります。</summary>
    /// <returns>旧入力表現の互換と新しい返却区分です。</returns>
    [Fact]
    public async Task LegacyStringContractRemainsExecutableAsync()
    {
        string code = "public static byte[] Run(byte[] data) => data;";
        JsonObject settings = Settings(code);
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code);
        ScriptContract legacy = new(new Dictionary<string, ValueTypeDefinition> { ["data"] = new("string") }, new("string"));
        settings["analysis"] = JsonSerializer.SerializeToNode(analysis with { Contract = legacy, ValueProtocolVersion = 0 }, ContractJson.Options);
        NodeResult result = await ExecuteAsync(settings, new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { data = "AAH/" }) });
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal(EValueKind.Binary, result.Outputs["value"].Kind);
        Assert.Equal(new byte[] { 0, 1, 255 }, result.Outputs["value"].Binary.ToArray());
        settings["analysis"] = JsonSerializer.SerializeToNode(analysis with { Contract = legacy, ValueProtocolVersion = 1 }, ContractJson.Options);
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(settings)).Outcome);
    }

    /// <summary>大きすぎる出力を、切れた正常なバイナリとして返しません。</summary>
    /// <returns>既存の出力上限の維持です。</returns>
    [Fact]
    public async Task BinaryResultsRespectWorkerOutputLimitAsync()
    {
        JsonObject settings = Settings("public static byte[] Run() => new byte[4096];");
        settings["maxBytes"] = 1024;
        NodeResult result = await ExecuteAsync(settings);
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>テスト出力に配置された実Coreをworkerとして使用します。</summary>
    /// <returns>標準のC#実行ノードです。</returns>
    private static IActionNode Node()
    {
        string name = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        return (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, name), [])).Single(node => node.Descriptor.Type is "script.csharp");
    }

    /// <summary>保存と同じ解析情報を作ります。</summary>
    /// <returns>コードを実行しない確定設定です。</returns>
    private static JsonObject Settings(string code)
    {
        ScriptAnalysis report = CSharpScriptCompiler.Analyze(code);
        Assert.Empty(report.Diagnostics);
        return new() { ["code"] = code, ["analysis"] = JsonSerializer.SerializeToNode(report, ContractJson.Options), ["inputMode"] = "object" };
    }

    /// <summary>本来の制限付きworker経路で一回だけ実行します。</summary>
    /// <returns>本物の子プロセスからの結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(JsonObject settings, IReadOnlyDictionary<string, NodeValue>? inputs = null, CancellationToken cancellationToken = default)
        => await Node().ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(false);
}
