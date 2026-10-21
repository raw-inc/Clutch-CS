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
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>実プロセスの実行と、開始前契約・期限を確認します。</summary>
public sealed class ScriptExecutionTests
{
    /// <summary>旧整数契約で保存済みのC#も、返却したlongの精度を保持してOSCへ渡せます。</summary>
    /// <returns>実worker、旧契約との比較、返却値の型保持です。</returns>
    [Theory]
    [InlineData("long", "1", "integer", "int64")]
    [InlineData("long[]", "new long[] { 1, 2 }", "array", "array")]
    [InlineData("Dictionary<long, long>", "new Dictionary<long, long> { [1] = 2 }", "object", "object")]
    public async Task ExistingInt64ContractsRetainPrecisionAsync(string type, string expression, string oldType, string newType)
    {
        string code = "public static " + type + " Run(" + type + " value) => value;";
        string producer = "public static " + type + " Run() => " + expression + ";";
        NodeResult produced = await ExecuteAsync("csharp", producer, new { }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, produced.Outcome);
        JsonObject settings = await SettingsAsync("csharp", code).ConfigureAwait(true);
        JsonNode oldContract = settings["analysis"]!["contract"]!;
        settings["analysis"]!["contract"] = JsonNode.Parse(oldContract.ToJsonString().Replace("\"int64\"", "\"integer\"", StringComparison.Ordinal));
        Assert.Equal(oldType, settings["analysis"]!["contract"]!["result"]!["type"]!.GetValue<string>());
        NodeResult result = await Node("csharp").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>
        { ["arguments"] = NodeValue.FromObject(new { value = produced.Outputs["value"].Json }) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(newType, result.Outputs["value"].DataType!.Type);
        string pointer = type switch { "long[]" => "/0", "Dictionary<long, long>" => "/1", _ => string.Empty };
        NodeValue number = JsonPointer.Select(result.Outputs["value"], pointer);
        Assert.Equal("int64", number.DataType!.Type);
        NodeResult packet = new Nodes.Communication.Osc.OscPacketNode().Evaluate(new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { })),
            new Dictionary<string, NodeValue> { ["address"] = NodeValue.FromObject("/script"), ["input1"] = number });
        Assert.Equal(EOutcome.Succeeded, packet.Outcome);
        Assert.Equal("h", packet.Outputs["value"].Json.GetProperty("arguments")[0].GetProperty("type").GetString());
    }

    /// <summary>C#のジェネリック辞書をキー型と値型まで推論して実プロセスへ渡します。</summary>
    /// <returns>文字列・整数・真偽値キーと、入れ子の型を保持した往復です。</returns>
    [Theory]
    [InlineData("string", "int", "{\"A\":1}", "string", "integer")]
    [InlineData("int", "string", "{\"1\":\"A\"}", "integer", "string")]
    [InlineData("bool", "string", "{\"true\":\"A\"}", "boolean", "string")]
    [InlineData("string", "Dictionary<int, string[]>", "{\"A\":{\"1\":[\"text\"]}}", "string", "object")]
    public async Task CSharpDictionariesRoundTripAsync(string key, string value, string json, string keyType, string valueType)
    {
        string code = "public static Dictionary<" + key + ", " + value + "> Run(Dictionary<" + key + ", " + value + "> values) => values;";
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(keyType, analysis.Contract!.Result.KeyType!.Type);
        Assert.Equal(valueType, analysis.Contract.Result.ValueType!.Type);
        NodeValue arguments = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("{\"values\":" + json + "}"));
        NodeResult result = await ExecuteAsync("csharp", code, arguments).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), result.Outputs["value"].Json));
        Assert.Equal(keyType, result.Outputs["value"].DataType!.KeyType!.Type);
    }

    /// <summary>辞書がobjectとして解析済みの既存定義も、再保存なしで実行できます。</summary>
    /// <returns>旧解析の入力と出力の互換性です。</returns>
    [Fact]
    public async Task ExistingCSharpDictionaryContractsRemainExecutableAsync()
    {
        string code = "public static Dictionary<string, int> Run(Dictionary<string, int> values) => values;";
        JsonObject settings = await SettingsAsync("csharp", code).ConfigureAwait(true);
        settings["analysis"]!["contract"] = JsonSerializer.SerializeToNode(new ScriptContract(new Dictionary<string, ValueTypeDefinition> { ["values"] = new("object") }, new("object")), ContractJson.Options);
        NodeResult result = await Node("csharp").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>
        { ["arguments"] = NodeValue.FromObject(new { values = new { A = 7 } }) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(7, result.Outputs["value"].Json.GetProperty("A").GetInt32());
    }

    /// <summary>辞書の型不一致と正規化で衝突するキーは、ユーザーコードを開始しません。</summary>
    /// <returns>キーと値の事前検証結果です。</returns>
    [Theory]
    [InlineData("{\"1\":\"wrong\"}")]
    [InlineData("{\"01\":2}")]
    public async Task InvalidDictionaryEntriesDoNotStartCSharpAsync(string json)
    {
        NodeValue arguments = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("{\"values\":" + json + "}"));
        NodeResult result = await ExecuteAsync("csharp", "public static int Run(Dictionary<int, int> values) => throw new Exception();", arguments).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
    }

    /// <summary>Python側では整数キーがintになり、JSONへ戻すときも型情報を保持します。</summary>
    /// <returns>型ヒントによる解析と実dict操作の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonDictionaryKeysAndNestedValuesAreTypedAsync()
    {
        string code = "def run(values: dict[int, list[str]]) -> dict[int, list[str]]:\n    assert all(type(key) is int for key in values)\n    values[2] = values[1] + ['second']\n    return values";
        NodeValue args = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("{\"values\":{\"1\":[\"first\"]}}"));
        NodeResult result = await ExecuteAsync("python", code, args).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("second", result.Outputs["value"].Json.GetProperty("2")[1].GetString());
        Assert.Equal("integer", result.Outputs["value"].DataType!.KeyType!.Type);
        Assert.Equal("array", result.Outputs["value"].DataType!.ValueType!.Type);
    }

    /// <summary>PathはC#側では普通のstringとして、実行OSの区切りで受け取れます。</summary>
    /// <returns>実C# workerの引数確認です。</returns>
    [Fact]
    public async Task CSharpStringArgumentAcceptsNativePathAsync()
    {
        NodeValue arguments = PathTypeMetadata.Apply(NodeValue.FromObject(new { file = "folder/clip.wav" }),
            new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["file"] = new("path") }));
        NodeResult result = await ExecuteAsync("csharp", "public static string Run(string file) => file;", arguments).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine("folder", "clip.wav"), result.Outputs["value"].GetString());
        Assert.Null(result.Outputs["value"].DataType);
        Assert.Equal("folder/clip.wav", arguments.Json.GetProperty("file").GetString());
    }

    /// <summary>Pythonにもstrを渡し、Pathクラスや言語固有のリテラル文字列は要求しません。</summary>
    /// <returns>実Pythonの引数確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonStringArgumentAcceptsNativePathAsync()
    {
        NodeValue arguments = PathTypeMetadata.Apply(NodeValue.FromObject(new { file = "folder/clip.wav" }),
            new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["file"] = new("path") }));
        NodeResult result = await ExecuteAsync("python", "def run(file: str) -> str:\n    return file", arguments).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine("folder", "clip.wav"), result.Outputs["value"].GetString());
    }

    /// <summary>型付き引数を受け取り、ユーザー標準出力と結果を混ぜません。</summary>
    /// <returns>通常ノードの結果確認です。</returns>
    [Fact]
    public async Task CSharpWorkerRunsTypedAsyncCodeAsync()
    {
        NodeResult result = await ExecuteAsync("csharp", "public static async Task<long> Run(long left, long right) { Console.WriteLine(\"ignored\"); await Task.Yield(); return left + right; }", new { left = 2, right = 3 }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(5, result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>子プロセスを止めても外部副作用の取消とは断定しません。</summary>
    /// <returns>期限超過の不明結果です。</returns>
    [Fact]
    public async Task CSharpWorkerDeadlineReturnsUnknownAsync()
    {
        NodeResult result = await ExecuteAsync("csharp", "public static async Task<int> Run() { await Task.Delay(60000); return 1; }", new { }, 500).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
    }

    /// <summary>コード例外と出力量超過を成功として扱いません。</summary>
    /// <returns>実行開始後の二つの境界です。</returns>
    [Fact]
    public async Task CSharpExceptionAndOutputOverflowDoNotSucceedAsync()
    {
        Assert.Equal(EOutcome.Failed, (await ExecuteAsync("csharp", "public static int Run() => throw new Exception(\"secret\");", new { }).ConfigureAwait(true)).Outcome);
        JsonObject settings = await SettingsAsync("csharp", "public static string Run() => new string('x', 100000);").ConfigureAwait(true);
        settings["maxBytes"] = 1024;
        Assert.Equal(EOutcome.Unknown, (await Node("csharp").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString())).ConfigureAwait(true)).Outcome);
    }

    /// <summary>型の違う固定引数は子プロセスを開始しません。</summary>
    /// <returns>実行前拒否です。</returns>
    [Fact]
    public async Task InvalidArgumentDoesNotExecuteAsync()
    {
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync("csharp", "public static int Run(int value) => value;", new { value = "invalid" }).ConfigureAwait(true)).Outcome);
    }

    /// <summary>解析後のコード差替えは実行しません。</summary>
    /// <returns>古い契約の拒否です。</returns>
    [Fact]
    public async Task ChangedCodeRequiresFreshAnalysisAsync()
    {
        JsonObject settings = await SettingsAsync("csharp", "public static int Run() => 1;").ConfigureAwait(true);
        settings["code"] = "public static string Run() => \"changed\";";
        NodeResult result = await Node("csharp").ExecuteAsync(NodeTestContext.Create(settings.ToJsonString())).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
    }

    /// <summary>型ヒントと式から結果を推論し、同じ契約で実行します。</summary>
    /// <returns>実Pythonの入出力確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonInfersAndExecutesTypedFunctionAsync()
    {
        NodeResult result = await ExecuteAsync("python", "def run(left: int, right: int):\n    print('ignored')\n    return left + right", new { left = 4, right = 7 }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(11, result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>ユーザー文を実行せず、不明引数を明示選択できます。</summary>
    /// <returns>未確定型と手動契約の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonAnalysisDoesNotExecuteCodeAndAllowsExplicitTypesAsync()
    {
        ScriptAnalysisRequest request = new("python", "raise RuntimeError('must not execute')\ndef run(value):\n    return value", PythonPath: Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON"));
        ScriptAnalysis unknown = await ScriptAnalysisService.AnalyzeAsync(request).ConfigureAwait(true);
        Assert.Equal("any", unknown.Contract!.Arguments["value"].Type);
        Assert.Empty(unknown.Diagnostics);
        Assert.NotNull(unknown.SuggestedContract);
        ScriptAnalysis selected = await ScriptAnalysisService.AnalyzeAsync(request with { ManualContract = JsonSerializer.SerializeToElement(new { arguments = new { value = new { type = "string" } }, result = new { type = "string" } }) }).ConfigureAwait(true);
        Assert.NotNull(selected.Contract);
        Assert.Empty(selected.Diagnostics);
    }

    /// <summary>テスト出力内のCoreをワーカーに指定します。</summary>
    /// <returns>通常登録された実行ノードです。</returns>
    private static IActionNode Node(string language)
    {
        string name = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        return (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, name), [])).Single(node => string.Equals(node.Descriptor.Type, "script." + language, StringComparison.Ordinal));
    }

    /// <summary>編集時と同じ解析を設定へ含めます。</summary>
    /// <returns>今回のコードに対応する設定です。</returns>
    private static async Task<JsonObject> SettingsAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        ScriptAnalysisRequest request = new(language, code);
        string python = "python";
        if (string.Equals(language, python, StringComparison.Ordinal))
        {
            request = request with { PythonPath = Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON") };
        }
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        JsonObject settings = new() { ["code"] = code, ["analysis"] = JsonSerializer.SerializeToNode(analysis, ContractJson.Options) };
        if (request.PythonPath is not null)
        {
            settings["pythonPath"] = request.PythonPath;
        }
        return settings;
    }

    /// <summary>固定引数と資源制限を与えます。</summary>
    /// <returns>実プロセスの結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(string language, string code, object arguments, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        JsonObject settings = await SettingsAsync(language, code, cancellationToken).ConfigureAwait(true);
        settings["timeoutMs"] = timeoutMs;
        return await Node(language).ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = arguments as NodeValue ?? NodeValue.FromObject(arguments) }), cancellationToken).ConfigureAwait(true);
    }
}
