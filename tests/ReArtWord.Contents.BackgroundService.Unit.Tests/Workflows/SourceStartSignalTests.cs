using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>受信の開始信号を待受の寿命や失敗結果と混同しません。</summary>
public sealed class SourceStartSignalTests
{
    /// <summary>他の入力や全経路接続があっても、正常な一件以外では起動しません。</summary>
    /// <returns>入力ごとの実行回数と、元結果を変更しないことの確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded, false, true)]
    [InlineData(EOutcome.Failed, false, false)]
    [InlineData(EOutcome.Unknown, false, false)]
    [InlineData(EOutcome.NotExecuted, false, false)]
    [InlineData(EOutcome.Cancelled, false, false)]
    [InlineData(EOutcome.Succeeded, true, false)]
    [InlineData(EOutcome.Failed, true, false)]
    [InlineData(EOutcome.Cancelled, true, false)]
    public async Task OnlySuccessfulInputStartsTheDownstreamAction(EOutcome outcome, bool terminal, bool starts)
    {
        List<NodeExecutionContext> calls = [];
        WorkflowFixture fixture = new(new InputSource(), new WorkflowFixture.ActionNode((context, _) =>
        {
            calls.Add(context);
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        CompiledWorkflow plan = compiled.Workflow!;
        NodeResult result = new(outcome, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject("payload"), ["start"] = NodeValue.FromObject(false),
        });
        for (int index = 0; index < 2; index++)
        {
            NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "receive", "a",
                definition.Nodes[0].Settings, new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System);
            ExecutionReport report = await fixture.Engine.ExecuteEmissionAsync(plan, new(context, result, terminal)).ConfigureAwait(true);
            NodeExecutionReport target = Assert.Single(report.Nodes, node => node.NodeId is "target");
            Assert.Equal(starts ? EOutcome.Succeeded
                                : EOutcome.NotExecuted, target.Outcome);
            NodeResult supplied = Assert.Single(Assert.Single(report.Nodes, node => node.NodeId is "receive").Targets).Result;
            Assert.Equal(starts, supplied.Outputs.ContainsKey("start"));
            Assert.Equal("payload", supplied.Outputs["value"].GetString());
        }
        Assert.Equal(starts ? 2
                            : 0, calls.Count);
        Assert.All(calls, call => Assert.True(call.Inputs["trigger"].Json.GetBoolean()));
        Assert.Equal(calls.Count, calls.Select(call => call.CausationId).Distinct().Count());
        Assert.False(result.Outputs["start"].Json.GetBoolean());
    }

    /// <summary>待受開始と、他ノードだけの実行を受信に見せかけません。</summary>
    /// <returns>実Source起動と停止を含む開始抑止の確認です。</returns>
    [Fact]
    public async Task StartingOrSkippingTheListenerDoesNotSupplyAStartSignal()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new InputSource(), new ConstantValueNode(), new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        NodeDefinition constant = new("constant", "value.constant", JsonSerializer.SerializeToElement(new { text = "data", type = "string" }));
        definition = definition with { Nodes = [.. definition.Nodes, constant],
            Wires = [definition.Wires[0], new("constant", "value", "target", "value")] };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "constant").ConfigureAwait(true);
        Assert.Equal(0, calls);
        _ = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "receive").ConfigureAwait(true);
        Assert.Equal(0, calls);
        _ = await fixture.Sources.StopAsync(definition.Id, "receive", cancel: true).ConfigureAwait(true);
        Assert.Equal(0, calls);
    }

    /// <summary>Sourceの共通信号を宣言し、通常の開始入力へ接続できます。</summary>
    [Fact]
    public void CatalogAndConnectionTypesExposeOneSignalWithoutChangingPayload()
    {
        NodeRegistry registry = new([new InputSource(), new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success()))]);
        NodeDescriptor descriptor = registry.Descriptors.Single(node => node.Kind == ENodeKind.Source);
        Assert.Single(descriptor.Outputs, port => port.Name is "start");
        Assert.Contains(descriptor.Outputs, port => port.Name is "value");
        Assert.Single(ConfiguredNodePorts.Resolve(descriptor, Definition().Nodes[0])!.Outputs, port => port.Name is "start");
        Assert.True(WorkflowConnectionTypes.CanConnect(Definition(), registry.Descriptors, Definition().Wires[0]));
        Assert.Equal("boolean", new WorkflowTypeInference(Definition(), registry.Descriptors).PortType("receive", "start", true)!.Type);
    }

    /// <summary>開始信号を必須条件とし、診断データは全経路で供給します。</summary>
    /// <returns>失敗・終了の迂回を再現する定義です。</returns>
    private static WorkflowDefinition Definition() => new("signal", "Source signal",
        [WorkflowFixture.Definition("receive", "test.source"), WorkflowFixture.Definition("target")],
        [new("receive", "start", "target", "trigger", "*"), new("receive", "result", "target", "value", "*")]);

    /// <summary>受信待ち自体は一件を発生させません。</summary>
    private sealed class InputSource : ISourceNode
    {
        public NodeDescriptor Descriptor => NodeSupport.Describe("test.source", "Source", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>試験入力に追加設定はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>取消まで待受だけを維持します。</summary>
        /// <returns>SourceRunnerが取消と終了を扱います。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return NodeResult.Success();
        }
    }
}
