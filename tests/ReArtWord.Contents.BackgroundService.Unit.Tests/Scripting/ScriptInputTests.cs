using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>個別入力と旧オブジェクト入力を同じ言語ワーカーで確認します。</summary>
public sealed class ScriptInputTests
{
    /// <summary>C#の既定値とparamsを個別・一括の両方式で適用します。</summary>
    /// <returns>未入力・null・単一値・配列の実ワーカー確認です。</returns>
    [Fact]
    public async Task CSharpDefaultsAndVariadicInputsWorkAsync()
    {
        await DefaultsAsync("csharp", "public static string Run(int count = 7, string text = \"hello\") => count + \":\" + text;").ConfigureAwait(true);
        await VariadicAsync("csharp", "public static int Run(int start = 10, params int[] values) => start + values.Sum();").ConfigureAwait(true);
    }

    /// <summary>Pythonの既定値と型ヒント付きargsにも同じ規則を適用します。</summary>
    /// <returns>実Pythonでの省略と可変長引数の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonDefaultsAndVariadicInputsWorkAsync()
    {
        await DefaultsAsync("python", "def run(count: int = 7, text: str = 'hello') -> str:\n    return str(count) + ':' + text").ConfigureAwait(true);
        await VariadicAsync("python", "def run(start: int = 10, *values: int) -> int:\n    return start + sum(values)").ConfigureAwait(true);
    }

    /// <summary>JavaScriptの既定値とrestにも同じ規則を適用します。</summary>
    /// <returns>実Node.jsでの省略と可変長引数の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task JavaScriptDefaultsAndVariadicInputsWorkAsync()
    {
        await DefaultsAsync("javascript", "export function run(count = 7, text = 'hello') { return count + ':' + text; }").ConfigureAwait(true);
        await VariadicAsync("javascript", "export function run(start = 10, ...values) { return start + values.reduce((sum, value) => sum + value, 0); }").ConfigureAwait(true);
    }

    /// <summary>C#のint・float・stringを個別ポートと実引数へ反映します。</summary>
    /// <returns>両方式の実行、型不一致と方式混在の開始前拒否です。</returns>
    [Fact]
    public async Task CSharpInputsAreTypedAndExclusiveAsync() => await CheckAsync("csharp", "public static string Run(int a, float b, string c) => a + b == 3.5f ? c : \"invalid\";").ConfigureAwait(true);

    /// <summary>Pythonの型ヒントを同じ個別入力契約へ反映します。</summary>
    /// <returns>実Pythonによる両方式の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonInputsAreTypedAndExclusiveAsync() => await CheckAsync("python", "def run(a: int, b: float, c: str) -> str:\n    return c if a + b == 3.5 else 'invalid'").ConfigureAwait(true);

    /// <summary>JavaScriptの既定値と未知の引数型を個別入力へ反映します。</summary>
    /// <returns>実Node.jsによる両方式の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task JavaScriptInputsAreTypedAndExclusiveAsync() => await CheckAsync("javascript", "export function run(a = 0, b = 0, c = '') { return a + b === 3.5 ? c : 'invalid'; }").ConfigureAwait(true);

    /// <summary>Pythonの型ヒントがない引数は、型選択なしで任意JSONを受け取ります。</summary>
    /// <returns>文字列・数値・構造・nullの実行結果です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PythonUnknownArgumentAcceptsAnyJsonAsync() => await UnknownAsync("python", "def run(value):\n    return value").ConfigureAwait(true);

    /// <summary>JavaScriptの未推定引数も、型選択なしで任意JSONを受け取ります。</summary>
    /// <returns>文字列・数値・構造・nullの実行結果です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task JavaScriptUnknownArgumentAcceptsAnyJsonAsync() => await UnknownAsync("javascript", "export function run(value) { return value; }").ConfigureAwait(true);

    /// <summary>C#のobjectはJSON値のanyとして受け取り、そのまま返せます。</summary>
    /// <returns>専用ワーカーを通過した任意JSONの確認です。</returns>
    [Fact]
    public async Task ObjectArgumentAcceptsAnyJsonAsync()
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync("csharp", "public static object Run(object trigger) => trigger;").ConfigureAwait(true);
        NodeDefinition definition = new("script", node.Descriptor.Type, JsonSerializer.SerializeToElement(settings));
        PortDescriptor port = Assert.Single(ConfiguredNodePorts.Resolve(node.Descriptor, definition)!.Inputs);
        Assert.Equal("$arguments.trigger", port.Name);
        Assert.Equal("any", port.DataType!.Type);
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { [port.Name] = NodeValue.FromObject(new { nested = new { item = 2 } }) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Outputs["value"].Json.GetProperty("nested").GetProperty("item").GetInt32());
    }

    /// <summary>可変長byteはbase64へ誤変換せず、要素の範囲を実行前に検査します。</summary>
    /// <returns>単一byte・配列・範囲外入力の実ワーカー確認です。</returns>
    [Fact]
    public async Task VariadicBytesAcceptNumbersAndRejectOverflowAsync()
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync("csharp", "public static int Run(params byte[] values) => values.Sum(value => (int)value);").ConfigureAwait(true);
        Assert.Equal(5, (await InvokeAsync(node, settings, true, "{\"values\":5}").ConfigureAwait(true)).Outputs["value"].Json.GetInt32());
        Assert.Equal(9, (await InvokeAsync(node, settings, false, "{\"values\":[4,5]}").ConfigureAwait(true)).Outputs["value"].Json.GetInt32());
        Assert.Equal(EOutcome.NotExecuted, (await InvokeAsync(node, settings, true, "{\"values\":256}").ConfigureAwait(true)).Outcome);
    }

    /// <summary>一括入力の単一Pathをparams配列へ変えても、実行OSの表記を維持します。</summary>
    /// <returns>入力方式の変換前後で失われないPathの意味です。</returns>
    [Fact]
    public async Task VariadicSinglePathKeepsNativePathMetadataAsync()
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync("csharp", "public static string Run(params string[] files) => files[0];").ConfigureAwait(true);
        settings["inputMode"] = "object";
        NodeValue supplied = NodeValue.FromObject(new { files = "folder/clip.wav" }) with { DataType = new("object", Properties: new Dictionary<string, Contracts.Values.ValueTypeDefinition>(StringComparer.Ordinal) { ["files"] = new("path") }) };
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = supplied })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine("folder", "clip.wav"), result.Outputs["value"].GetString());
    }

    /// <summary>現在の解析に一致する型だけを公開し、古いコードのポートを使いません。</summary>
    /// <returns>公開契約・実行・旧形式の回帰確認です。</returns>
    private static async Task CheckAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync(language, code, cancellationToken).ConfigureAwait(true);
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            [ScriptInputPorts.Name("a")] = NodeValue.FromObject(1),
            [ScriptInputPorts.Name("b")] = NodeValue.FromObject(2.5),
            [ScriptInputPorts.Name("c")] = NodeValue.FromObject("hello"),
        };
        NodeDefinition definition = new("script", node.Descriptor.Type, JsonSerializer.SerializeToElement(settings), inputs);
        NodeDescriptor ports = ConfiguredNodePorts.Resolve(node.Descriptor, definition)!;
        Assert.Equal(3, ports.Inputs.Count);
        Assert.Equal("number", ports.Inputs[1].DataType!.Type);
        Assert.Equal("string", ScriptSettings.PortType(definition, ScriptInputPorts.Name("c"), false)!.Type);
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("hello", result.Outputs["value"].GetString());
        inputs[ScriptInputPorts.Name("a")] = NodeValue.FromObject("invalid");
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true)).Outcome);
        inputs[ScriptInputPorts.Name("a")] = NodeValue.FromObject(1);
        inputs["arguments"] = NodeValue.FromObject(new { a = 1, b = 2.5, c = "hello" });
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true)).Outcome);
        settings.Remove("inputMode");
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true)).Outcome);
        result = await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = inputs["arguments"] }), cancellationToken).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("hello", result.Outputs["value"].GetString());
        settings["inputMode"] = "individual";
        settings["code"] = code + " ";
        Assert.Empty(ConfiguredNodePorts.Resolve(node.Descriptor, definition with { Settings = JsonSerializer.SerializeToElement(settings) })!.Inputs);
    }

    /// <summary>明示nullと未入力を既定値へ変え、0や空文字はそのまま渡します。</summary>
    /// <returns>非null型の既定値付き引数が任意入力になることです。</returns>
    private static async Task DefaultsAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync(language, code, cancellationToken).ConfigureAwait(true);
        NodeDefinition definition = new("script", node.Descriptor.Type, JsonSerializer.SerializeToElement(settings));
        Assert.All(ConfiguredNodePorts.Resolve(node.Descriptor, definition)!.Inputs, port => Assert.False(port.Required));
        foreach (bool individual in new[] { true, false })
        {
            foreach (string arguments in new[] { "{}", "{\"count\":null,\"text\":null}", "{\"count\":0,\"text\":\"\"}" })
            {
                NodeResult result = await InvokeAsync(node, settings, individual, arguments, cancellationToken).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                string expected = arguments.Contains('0', StringComparison.Ordinal) ? "0:"
                                                                                    : "7:hello";
                Assert.Equal(expected, result.Outputs["value"].GetString());
            }
        }
        ScriptAnalysis report = ScriptSettings.Confirmed(definition.Type, definition.Settings)!;
        ScriptContract legacy = report.Contract! with { Arguments = report.Contract!.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value with { Optional = false }, StringComparer.Ordinal) };
        settings["analysis"] = JsonSerializer.SerializeToNode(report with { Contract = legacy }, ContractJson.Options);
        Assert.Equal(EOutcome.Succeeded, (await InvokeAsync(node, settings, false, "{\"count\":0,\"text\":\"\"}", cancellationToken).ConfigureAwait(true)).Outcome);
    }

    /// <summary>未推定型を両入力方式から渡しても、JSONの種類と内容を変えません。</summary>
    /// <returns>anyとしての公開契約と実行結果の確認です。</returns>
    private static async Task UnknownAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync(language, code, cancellationToken).ConfigureAwait(true);
        Assert.Equal("any", ScriptSettings.Confirmed(node.Descriptor.Type, JsonSerializer.SerializeToElement(settings))!.Contract!.Arguments["value"].Type);
        foreach (bool individual in new[] { true, false })
        {
            foreach (string json in new[] { "7", "\"text\"", "{\"nested\":[true,1]}", "null" })
            {
                NodeResult result = await InvokeAsync(node, settings, individual, "{\"value\":" + json + "}", cancellationToken).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                using JsonDocument expected = JsonDocument.Parse(json);
                Assert.True(JsonElement.DeepEquals(expected.RootElement, result.Outputs["value"].Json));
            }
        }
    }

    /// <summary>params・args・restの単一値を一要素へ変換し、未入力とnullは空配列にします。</summary>
    /// <returns>既定値付きの先行引数と可変長引数の組合せです。</returns>
    private static async Task VariadicAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        (IActionNode node, JsonObject settings) = await PrepareAsync(language, code, cancellationToken).ConfigureAwait(true);
        foreach (bool individual in new[] { true, false })
        {
            foreach ((string arguments, int expected) in new[] { ("{}", 10), ("{\"values\":null}", 10), ("{\"values\":4}", 14), ("{\"values\":[4,5]}", 19) })
            {
                NodeResult result = await InvokeAsync(node, settings, individual, arguments, cancellationToken).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                Assert.Equal(expected, result.Outputs["value"].Json.GetInt32());
            }
        }
    }

    /// <summary>同じ入力値を二つの公開方式へ配置して通常のノードを実行します。</summary>
    /// <returns>一回の実行結果です。</returns>
    private static async Task<NodeResult> InvokeAsync(IActionNode node, JsonObject settings, bool individual, string arguments, CancellationToken cancellationToken = default)
    {
        settings["inputMode"] = individual ? "individual"
                                           : "object";
        using JsonDocument document = JsonDocument.Parse(arguments);
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (individual)
        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                inputs.Add(ScriptInputPorts.Name(property.Name), NodeValue.FromJson(property.Value));
            }
        }
        else
        {
            inputs.Add("arguments", NodeValue.FromJson(document.RootElement));
        }
        return await node.ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), extra: inputs), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>実際の言語解析と、テスト出力内のCoreワーカーを準備します。</summary>
    /// <returns>解析に対応するノード設定です。</returns>
    private static async Task<(IActionNode Node, JsonObject Settings)> PrepareAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        ScriptAnalysisRequest request = new(language, code);
        string python = "python", javascript = "javascript";
        if (string.Equals(language, python, StringComparison.Ordinal))
        {
            request = request with { PythonPath = Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON") };
        }
        if (string.Equals(language, javascript, StringComparison.Ordinal))
        {
            request = request with { NodePath = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE") };
        }
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        string executable = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            executable += ".exe";
        }
        IActionNode node = (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, executable), [])).Single(item => string.Equals(item.Descriptor.Type, "script." + language, StringComparison.Ordinal));
        JsonObject settings = new() { ["code"] = code, ["inputMode"] = "individual", ["analysis"] = JsonSerializer.SerializeToNode(analysis, ContractJson.Options) };
        if (request.PythonPath is not null)
        {
            settings["pythonPath"] = request.PythonPath;
        }
        if (request.NodePath is not null)
        {
            settings["nodePath"] = request.NodePath;
        }
        return (node, settings);
    }
}
