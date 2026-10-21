using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>ボタンから要求された値を条件分岐・合流越しに読み、別の開始事象を生成しません。</summary>
public sealed class ConditionalValueDemandTests
{
    /// <summary>未押下のトグルも現在値を供給し、切替後の読み取りで旧分岐を流用しません。</summary>
    /// <returns>実際の後続呼出しと、合流Signalに接続した副作用の不発確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ButtonReadsTheSelectedBranchWithoutEmittingAnotherEventAsync(bool iteration, bool combinedIf)
    {
        List<string?> reads = [];
        int changes = 0;
        WorkflowFixture fixture = Fixture((context, _) =>
        {
            string consumer = "consumer";
            if (string.Equals(context.NodeId, consumer, StringComparison.Ordinal))
            {
                reads.Add(context.Inputs["value"].GetString());
            }
            else
            {
                changes++;
            }
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        WorkflowDefinition definition = Definition();
        if (combinedIf)
        {
            string off = "off", on = "on";
            definition = definition with
            {
                Nodes = definition.Nodes.Where(node => !string.Equals(node.Id, off, StringComparison.Ordinal)).Select(node =>
                {
                    if (string.Equals(node.Id, on, StringComparison.Ordinal))
                    {
                        return node with { Type = "control.if" };
                    }
                    return node;
                }).ToArray(),
                Wires = definition.Wires.Where(wire => !string.Equals(wire.ToNode, off, StringComparison.Ordinal)).Select(wire => wire.FromNode switch
                {
                    "off" => wire with { FromNode = on, FromPort = "falseStart" }, "on" => wire with { FromPort = "trueStart" }, _ => wire,
                }).ToArray(),
            };
        }
        if (iteration)
        {
            NodeDefinition loop = Node("unrelated-loop", "control.forEach") with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(Array.Empty<int>()) } };
            definition = definition with { Nodes = [.. definition.Nodes, loop] };
        }
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        CompiledWorkflow workflow = compiled.Workflow!;
        ExecutionReport first = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal(["selected=stub"], reads);
        Assert.Equal(0, changes);
        _ = await fixture.Engine.ExecuteAsync(workflow, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        int signaledChanges = changes;
        Assert.Equal(1, signaledChanges);
        for (int index = 0; index < 2; index++)
        {
            _ = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
            Assert.Equal("selected=production", reads[^1]);
            Assert.Equal(signaledChanges, changes);
        }
        Assert.Equal(3, reads.Count);
    }

    /// <summary>合流先の値要求で、信号待ちの処理や未押下ボタンを起動しません。</summary>
    /// <returns>待受としての合流と、値参照の境界です。</returns>
    [Theory]
    [InlineData("control.button")]
    [InlineData("test.action")]
    public async Task ReadingMergeDoesNotStartUnrelatedActionsAsync(string sourceType)
    {
        int calls = 0;
        WorkflowFixture fixture = Fixture((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("unrelated-merge", "Unrelated merge",
            [Node("read", "control.button"), Node("other", sourceType), Node("merge", "control.valueMerge"), Node("consumer", "test.action")],
            [new("read", "start", "consumer", "trigger"), new("other", "value", "merge", "input1"), new("merge", "value", "consumer", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "read").ConfigureAwait(true);
        Assert.Equal(0, calls);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Contains(report.Nodes.SelectMany(node => node.Targets), target => target.Result.ReasonCode is "input.unavailable");
    }

    /// <summary>同時に二つの候補が有効な値参照は、配線順で黙って一つを選びません。</summary>
    /// <returns>副作用を開始しない曖昧な入力の報告です。</returns>
    [Fact]
    public async Task AmbiguousValuesFailWithoutStartingTheConsumerAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Fixture((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("ambiguous-values", "Ambiguous values",
            [Node("read", "control.button"), Node("first", "value.constant", """{"text":"first"}"""),
             Node("second", "value.constant", """{"text":"second"}"""), Node("merge", "control.valueMerge"), Node("consumer", "test.action")],
            [new("read", "start", "consumer", "trigger"), new("first", "value", "merge", "input1"),
             new("second", "value", "merge", "input2"), new("merge", "value", "consumer", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "read").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Contains(report.Nodes.SelectMany(node => node.Targets), target => target.Result.ReasonCode is "valueMerge.ambiguous");
        Assert.Equal(0, calls);
    }

    /// <summary>条件付き値・連結・合流に必要な実ノードを登録します。</summary>
    /// <returns>副作用だけを観測する実エンジンです。</returns>
    private static WorkflowFixture Fixture(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> observe)
        => new(new ButtonNode(), new ToggleButtonNode(new TestStateStore()), new IfNode(), new IfNode(true), new IfElseNode(), new ValueMergeNode(),
            new ConstantValueNode(), new ConcatNode(), new ForEachNode(), new WorkflowFixture.ActionNode(observe));

    /// <summary>HTTPのURL組立てと同じボタン・条件付き定数・合流・連結の接続です。</summary>
    /// <returns>別の副作用へ出るSignalも持つグラフです。</returns>
    private static WorkflowDefinition Definition() => new("conditional-values", "Conditional values",
        [Node("read", "control.button"), Node("toggle", "control.toggleButton") with
        {
            Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject(false), ["onValue"] = NodeValue.FromObject(true) },
        }, Node("off", "control.ifNotStart"), Node("on", "control.ifStart"),
         Node("stub", "value.constant", """{"text":"stub"}"""), Node("production", "value.constant", """{"text":"production"}"""),
         Node("prefix", "value.constant", """{"text":"selected="}"""), Node("merge", "control.valueMerge"), Node("concat", "data.concat"),
         Node("consumer", "test.action"), Node("changed", "test.action")],
        [new("read", "start", "consumer", "trigger"), new("toggle", "value", "off", "condition"), new("toggle", "value", "on", "condition"),
         new("off", "start", "stub", "trigger"), new("on", "start", "production", "trigger"),
         new("stub", "value", "merge", "input1"), new("production", "value", "merge", "input2"),
         new("prefix", "value", "concat", "input1"), new("merge", "value", "concat", "input2"), new("concat", "value", "consumer", "value"),
         new("merge", "start", "changed", "trigger")]);

    /// <summary>公開された設定から最小のノードを作ります。</summary>
    /// <returns>試験用ノードです。</returns>
    private static NodeDefinition Node(string id, string type, string settings = "{}") => WorkflowFixture.Definition(id, type, settings);
}
