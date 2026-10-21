using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>任意設定の省略を、未到着・失敗やPC候補の不足から分離します。</summary>
public sealed class OptionalSettingsBindingTests
{
    /// <summary>成功した入力の項目欠落とnullは保存値へ戻さず、ゼロとfalseは保持します。</summary>
    /// <returns>静的評価と実行時評価に共通する省略規則です。</returns>
    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"instruction\":null,\"volume\":null,\"flag\":null}")]
    [InlineData(true, "{\"instruction\":null,\"volume\":null,\"flag\":null}")]
    [InlineData(false, "{\"instruction\":\"\",\"volume\":0,\"flag\":false}")]
    [InlineData(true, "{\"instruction\":\"\",\"volume\":0,\"flag\":false}")]
    public async Task SuccessfulOptionalValuesHaveTheSameStaticAndDynamicMeaningAsync(bool dynamic, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        WorkflowFixture fixture = new(new ConstantValueNode(), new ButtonNode(), new Probe());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "object", text = json }));
        if (dynamic)
        {
            source = WorkflowFixture.Definition("source", "control.button");
        }
        NodeDefinition target = WorkflowFixture.Definition("target", "test.optional-settings", """{"required":"fixed","instruction":"old","volume":80,"flag":true}""");
        WorkflowDefinition definition = new("optional", "optional", [source, target],
            [new("source", "value", "target", "$settings.instruction", Path: "/instruction"), new("source", "value", "target", "$settings.volume", Path: "/volume"), new("source", "value", "target", "$settings.flag", Path: "/flag")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        IReadOnlyDictionary<string, NodeValue>? inputs = null;
        if (dynamic)
        {
            inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json)) };
        }
        string entry = "target";
        if (dynamic)
        {
            entry = "source";
        }
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, entry, inputs).ConfigureAwait(true);
        NodeExecutionReport executed = Assert.Single(report.Nodes, node => node.NodeId is "target");
        Assert.Equal(EOutcome.Succeeded, executed.Outcome);
        JsonElement settings = Assert.Single(executed.Targets).Result.Outputs["value"].Json;
        bool explicitValues = json.Contains("false", StringComparison.Ordinal);
        foreach (string name in new[] { "instruction", "volume", "flag" })
        {
            Assert.Equal(explicitValues, settings.TryGetProperty(name, out _));
        }
        if (explicitValues)
        {
            Assert.Equal(string.Empty, settings.GetProperty("instruction").GetString());
            Assert.Equal(0, settings.GetProperty("volume").GetInt32());
            Assert.False(settings.GetProperty("flag").GetBoolean());
        }
        Assert.Equal("old", target.Settings.GetProperty("instruction").GetString());
    }

    /// <summary>設定不足があっても、正常に到着したPC候補を未到着と報告しません。</summary>
    /// <returns>PC候補とは別の必須設定不足として未実行になる確認です。</returns>
    [Fact]
    public async Task MissingRequiredSettingDoesNotBecomeAMissingPcErrorAsync()
    {
        WorkflowFixture fixture = new(new ButtonNode(), new PcSelectionNode(), new ValueMergeNode(), new Probe());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("required", "required", [WorkflowFixture.Definition("source", "control.button"),
            WorkflowFixture.Definition("pc", PcSelectionPorts.SelectNode, """{"mode":"selected","hostIds":["host-a"]}"""),
            WorkflowFixture.Definition("merge", ValueMergePorts.NodeType), WorkflowFixture.Definition("target", "test.optional-settings", """{"required":"old"}""")],
            [new("source", "start", "pc", "trigger"), new("pc", "value", "merge", "input1"), new("merge", "value", "target", PcSelectionPorts.Execution),
                new("source", "value/required", "target", "$settings.required")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new { }) }).ConfigureAwait(true);
        NodeExecutionReport target = Assert.Single(report.Nodes, node => node.NodeId is "target");
        Assert.Equal(EOutcome.NotExecuted, target.Outcome);
        Assert.Equal("input.unavailable", Assert.Single(target.Targets).Result.ReasonCode);
        Assert.Equal("host-a", Assert.Single(target.PcSelection!.CandidateHostIds));
    }

    /// <summary>失敗・結果不明・未実行や出力の欠損を、任意プロパティの省略として通しません。</summary>
    /// <returns>上流が供給できなかった場合は利用側を実行しない確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    [InlineData(EOutcome.NotExecuted)]
    [InlineData(EOutcome.Succeeded)]
    public async Task MissingOrFailedUpstreamIsNotAnAbsentParameterAsync(EOutcome outcome)
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(new NodeResult(outcome, "success", new Dictionary<string, NodeValue>()))), new Probe());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("missing", "missing", [WorkflowFixture.Definition("source"), WorkflowFixture.Definition("target", "test.optional-settings", """{"required":"fixed"}""")],
            [new("source", "value/instruction", "target", "$settings.instruction")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "target").Outcome);
    }

    /// <summary>全結果経路を選んでも、失敗した出力の項目欠落は任意値の省略になりません。</summary>
    /// <returns>失敗本文を空パラメーターとして処理しない確認です。</returns>
    [Fact]
    public async Task FailedWildcardOutputDoesNotEraseAnOptionalSettingAsync()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new { })) with
        {
            Outcome = EOutcome.Failed, Route = "failure",
        })), new Probe());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("failed", "failed", [WorkflowFixture.Definition("source"), WorkflowFixture.Definition("target", "test.optional-settings", """{"required":"fixed"}""")],
            [new("source", "value/instruction", "target", "$settings.instruction", "*")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "target").Outcome);
    }

    /// <summary>公開スキーマで省略を明示し、実行時に渡された設定を観測します。</summary>
    private sealed class Probe : IActionNode
    {
        /// <summary>必須設定の省略は許可せず、任意設定だけを宣言します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.optional-settings", "optional", "Test", """
            {"type":"object","required":["required"],"properties":{"required":{"type":"string","minLength":1},
            "instruction":{"type":"string","x-omit-if-null":true},"volume":{"type":"integer","minimum":0,"maximum":100,"x-omit-if-null":true},
            "flag":{"type":"boolean","x-omit-if-null":true}}}
            """, inputs: []);

        /// <summary>解決後の必須・型・範囲を検証します。</summary>
        /// <returns>不正設定です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSchemaValidator.Validate(definition, Descriptor);

        /// <summary>副作用を起こさず、受け取った設定だけを返します。</summary>
        /// <returns>実行時設定です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NodeResult.Success(NodeValue.FromJson(context.Settings)));
    }
}
