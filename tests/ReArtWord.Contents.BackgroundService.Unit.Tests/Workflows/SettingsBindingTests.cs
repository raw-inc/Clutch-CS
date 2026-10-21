using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定接続の静的評価と保存・実行定義の分離を検証します。</summary>
public sealed class SettingsBindingTests
{
    /// <summary>未選択または古い保存設定のまま、解決値を検証して根/部分実行に使います。</summary>
    /// <returns>実NodeExecutorと実グラフ実行の結果です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("target")]
    public async Task OriginalDefinitionAndRevisionStayRawWhileExecutionUsesResolvedSettings(string? entry)
    {
        WorkflowFixture fixture = new(new ConstantValueNode(), new SettingsProbeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition("21", "integer", """{"port":0,"label":"original"}""");
        NodeDefinition target = definition.Nodes[1] with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { [SettingPorts.Name("label")] = NodeValue.FromObject("wired") } };
        definition = definition with { Nodes = [definition.Nodes[0], target] };
        string original = JsonSerializer.Serialize(definition, ContractJson.Options);
        string revision = ContractJson.Hash(definition);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        CompiledWorkflow plan = compiled.Workflow!;
        Assert.Equal(revision, plan.Revision);
        Assert.Equal(original, JsonSerializer.Serialize(plan.Definition, ContractJson.Options));
        Assert.Equal(original, JsonSerializer.Serialize(definition, ContractJson.Options));
        Assert.Equal(0, plan.Definition.Nodes[1].Settings.GetProperty("port").GetInt32());
        Assert.Equal(21, plan.FindNode("target")!.Settings.GetProperty("port").GetInt32());
        Assert.Empty(plan.Incoming["target"]);
        Assert.False(plan.FindNode("target")!.Inputs!.ContainsKey(SettingPorts.Name("label")));
        ExecutionReport result = await fixture.Engine.ExecuteAsync(plan, entry);
        NodeExecutionReport executed = Assert.Single(result.Nodes, node => string.Equals(node.NodeId, "target", StringComparison.Ordinal));
        Assert.Equal(EOutcome.Succeeded, executed.Outcome);
        JsonElement used = Assert.Single(executed.Targets).Result.Outputs["value"].Json;
        Assert.Equal(21, used.GetProperty("port").GetInt32());
        Assert.Equal("wired", used.GetProperty("label").GetString());
    }

    /// <summary>接続を外すと元の保存設定を再び使い、固有Validateは上書き値で評価します。</summary>
    [Fact]
    public void UnbindingRestoresStoredSettingAndNativeValidationUsesOverlay()
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new ConstantValueNode(), new SettingsProbeNode()]));
        WorkflowDefinition definition = Definition("21", "integer", """{"port":7}""");
        Assert.Equal(21, compiler.Compile(definition).Workflow!.FindNode("target")!.Settings.GetProperty("port").GetInt32());
        Assert.Equal(7, compiler.Compile(definition with { Wires = [] }).Workflow!.FindNode("target")!.Settings.GetProperty("port").GetInt32());
        CompilationResult invalid = compiler.Compile(Definition("13", "integer", "{}"));
        Assert.Null(invalid.Workflow);
        Assert.Contains(invalid.Issues, issue => string.Equals(issue.Code, "settings.probe.reserved", StringComparison.Ordinal));
    }

    /// <summary>JSONとして供給されてもスキーマの型・範囲を満たさない設定は開始しません。</summary>
    [Theory]
    [InlineData("200", "integer")]
    [InlineData("wrong", "string")]
    [InlineData("null", "auto")]
    public void InvalidBoundSettingsCannotCompile(string text, string type)
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new ConstantValueNode(), new SettingsProbeNode()]));
        CompilationResult result = compiler.Compile(Definition(text, type, "{}"));
        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "settings.value", StringComparison.Ordinal));
    }

    /// <summary>nullable設定でもPointer欠損と明示nullを区別し、通常Pointerの意味は維持します。</summary>
    [Fact]
    public void MissingPointerIsNotAnExplicitNullSetting()
    {
        WorkflowCompiler compiler = new(new NodeRegistry([new ConstantValueNode(), new SettingsProbeNode()]));
        WorkflowDefinition missing = Definition("{}", "object", """{"port":7}""") with { Wires = [new("value", "value", "target", SettingPorts.Name("optional"), Path: "/optional")] };
        Assert.Contains(compiler.Compile(missing).Issues, issue => string.Equals(issue.Code, "settings.binding.missing", StringComparison.Ordinal));
        NodeDefinition source = missing.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { text = "{\"optional\":null}", type = "object" }) };
        CompilationResult supplied = compiler.Compile(missing with { Nodes = [source, missing.Nodes[1]] });
        Assert.Empty(supplied.Issues);
        Assert.Equal(JsonValueKind.Null, supplied.Workflow!.FindNode("target")!.Settings.GetProperty("optional").ValueKind);
        Assert.Equal(JsonValueKind.Null, JsonPointer.Select(NodeValue.FromObject(new { }), "/missing").Json.ValueKind);
        Assert.False(JsonPointer.TrySelect(NodeValue.FromObject(new { }), "/missing", out _));
    }

    /// <summary>動的設定は構成時に実行せず、毎回の値で固有検証も通してから配送します。</summary>
    /// <returns>保存設定の不変性と、供給後の成否です。</returns>
    [Theory]
    [InlineData(21, EOutcome.Succeeded)]
    [InlineData(13, EOutcome.NotExecuted)]
    [InlineData(101, EOutcome.NotExecuted)]
    public async Task DynamicSettingsAreResolvedAndValidatedBeforeExecutionAsync(int value, EOutcome expected)
    {
        int calls = 0;
        WorkflowFixture.ActionNode dynamic = new((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(value)));
        });
        WorkflowFixture fixture = new(dynamic, new SettingsProbeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition("21", "integer", "{}") with { Nodes = [WorkflowFixture.Definition("value"), new("target", "test.settings", JsonSerializer.SerializeToElement(new { port = 0 }))] };
        CompilationResult result = fixture.Compiler.Compile(definition);
        Assert.Empty(result.Issues);
        Assert.Equal(0, calls);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(result.Workflow!, "value").ConfigureAwait(true);
        Assert.Equal(expected, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "target", StringComparison.Ordinal)).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0, definition.Nodes[1].Settings.GetProperty("port").GetInt32());
    }

    /// <summary>純粋値でも実行時に届く文字列を設定から変換し、以前の値をキャッシュしません。</summary>
    /// <returns>同じ計画の二回目にも、その回の設定値を使う確認です。</returns>
    [Fact]
    public async Task PureNodeSettingsCanDependOnRuntimeValuesAsync()
    {
        int next = 21;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject((next++).ToString(System.Globalization.CultureInfo.InvariantCulture))))), new ConstantValueNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("flow", "Runtime value construction",
            [WorkflowFixture.Definition("source"), WorkflowFixture.Definition("value", "value.constant", """{"type":"integer"}""")], [new("source", "value", "value", SettingPorts.Name("text"))]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        for (int expected = 21; expected < 23; expected++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
            NodeExecutionReport target = Assert.Single(report.Nodes, item => string.Equals(item.NodeId, "value", StringComparison.Ordinal));
            Assert.Equal(EOutcome.Succeeded, target.Outcome);
            Assert.Equal(expected, Assert.Single(target.Targets).Result.Outputs["value"].Json.GetInt32());
        }
    }

    /// <summary>動的Pointerの欠損を明示nullや以前の固定入力へ読み替えません。</summary>
    /// <returns>nullable設定にも未供給の接続は失敗として残る確認です。</returns>
    [Theory]
    [InlineData("{}", EOutcome.NotExecuted)]
    [InlineData("{\"optional\":null}", EOutcome.Succeeded)]
    public async Task RuntimeMissingPointerDoesNotFallBackAsync(string json, EOutcome expected)
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json))))), new SettingsProbeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition target = WorkflowFixture.Definition("target", "test.settings", """{"port":21,"optional":"saved"}""");
        WorkflowDefinition definition = new("flow", "Missing setting", [WorkflowFixture.Definition("source"), target], [new("source", "value", "target", SettingPorts.Name("optional"), Path: "/optional")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(expected, Assert.Single(report.Nodes, item => string.Equals(item.NodeId, target.Id, StringComparison.Ordinal)).Outcome);
    }

    /// <summary>pure宣言だけで未入力・Binary・失敗出力を成功設定にしません。</summary>
    [Theory]
    [InlineData("missing", "settings.binding.missing")]
    [InlineData("binary", "settings.binding.type")]
    [InlineData("failed", "settings.binding.static")]
    public void PureSourceMustSupplySuccessfulJsonValue(string kind, string code)
    {
        NodeResult value = kind switch
        {
            "binary" => NodeResult.Success(NodeValue.FromBytes(new byte[] { 1, 2 })),
            "failed" => NodeResult.Success(NodeValue.FromObject(21)) with { Outcome = EOutcome.Failed },
            _ => NodeResult.Success(),
        };
        WorkflowCompiler compiler = new(new NodeRegistry([new SettingsValueProbeNode(value), new SettingsProbeNode()]));
        WorkflowDefinition definition = Definition("21", "integer", "{}") with
        {
            Nodes = [new("value", "test.value", JsonSerializer.SerializeToElement(new { })), new("target", "test.settings", JsonSerializer.SerializeToElement(new { }))],
            Wires = [new("value", "value", "target", SettingPorts.Name("port"), "*")],
        };
        CompilationResult result = compiler.Compile(definition);
        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, code, StringComparison.Ordinal));
    }

    /// <summary>保存する元設定と純粋値の接続を構成します。</summary>
    /// <returns>設定ポートを持つグラフです。</returns>
    internal static WorkflowDefinition Definition(string text, string type, string settings) => new("settings", "settings binding",
        [new("value", "value.constant", JsonSerializer.SerializeToElement(new { text, type })), new("target", "test.settings", JsonSerializer.Deserialize<JsonElement>(settings))],
        [new("value", "value", "target", SettingPorts.Name("port"))]);
}
