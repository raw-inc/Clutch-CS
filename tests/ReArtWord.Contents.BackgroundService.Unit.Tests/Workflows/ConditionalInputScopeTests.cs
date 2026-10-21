using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>レシピ・反復・合流の境界でも、非選択を別要求の開始や入力不足に変えません。</summary>
public sealed class ConditionalInputScopeTests
{
    private static readonly bool[] selections = [false, true, false, true];

    /// <summary>SubWorkflowの公開出力を通った開始信号も、宛先の非選択理由を維持します。</summary>
    /// <returns>レシピの内外で同じ分岐結果になる確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecipeOutputPreservesTheBranchDecisionAsync(bool selected)
    {
        ConditionalInputBranchTests.Sink sink = new(true);
        WorkflowFixture fixture = ConditionalInputBranchTests.Create(sink, new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        WorkflowDefinition recipe = new("choose", "Choose", [Field("input", false, "condition"), Field("output", true, "selected"), WorkflowFixture.Definition("choose", "control.ifStart")],
            [new("input", "value", "choose", "condition"), new("choose", "start", "output", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        WorkflowDefinition definition = ConditionalInputBranchTests.Definition(true, selected);
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Id switch { "gate" => node with { Type = SubworkflowInterface.CallPrefix + recipe.Id }, _ => node }).ToArray(),
            Wires = definition.Wires.Select(wire => wire.FromNode switch { "gate" => wire with { FromPort = "selected" }, _ => wire }).ToArray(),
        };
        ExecutionReport result = await ConditionalInputBranchTests.RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(selected ? 1
                              : 0, sink.Calls);
        if (!selected)
        {
            Assert.Equal(ExecutionResultReasons.BranchNotSelected, ConditionalInputBranchTests.Result(result, "sink").ReasonCode);
        }
    }

    /// <summary>反復要素の分岐状態を混ぜず、全候補が非選択の値合流も正常に通過扱いしません。</summary>
    /// <returns>選択された要素だけが副作用へ到達し、非選択は反復を失敗させない確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IterationUsesItsOwnBranchDecisionAsync(bool parallel, bool merge)
    {
        ConditionalInputBranchTests.Sink sink = new(true);
        WorkflowFixture fixture = ConditionalInputBranchTests.Create(sink, new ForEachNode(), new ForEachNode(true), new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition original = ConditionalInputBranchTests.Definition(true, true);
        string loopType = IterationNodeTypes.ForEach;
        if (parallel)
        {
            loopType = IterationNodeTypes.ParallelForEach;
        }
        NodeDefinition loop = WorkflowFixture.Definition("loop", loopType) with
        {
            Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(selections) },
        };
        WorkflowDefinition definition = original with
        {
            Nodes = [.. original.Nodes.Select(node => node.Id switch { "gate" => node with { Inputs = null }, _ => node }), loop],
            Wires = [.. original.Wires.Where(wire => wire.FromNode is not "source"), new("source", "start", "loop", "trigger"),
                new("loop", "item", "gate", "condition"), new("loop", "item", "sink", "value")],
        };
        if (merge)
        {
            definition = definition with
            {
                Nodes = [.. definition.Nodes, WorkflowFixture.Definition("merge", ValueMergePorts.NodeType)],
                Wires = [.. definition.Wires.Select(wire => wire.FromNode switch { "concat" => wire with { ToNode = "merge", ToPort = "input1" }, _ => wire }),
                    new("merge", "value", "sink", "$settings.remoteAddress")],
            };
        }
        ExecutionReport result = await ConditionalInputBranchTests.RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, sink.Calls);
        IterationExecutionSummary summary = Assert.Single(result.Nodes, node => node.NodeId is "loop").Iteration!;
        Assert.Equal(4, summary.Started);
        foreach (IterationExecutionResult item in summary.Items)
        {
            NodeResult received = ConditionalInputBranchTests.Result(item.Execution.Report!, "sink");
            if (item.Index % 2 == 0)
            {
                Assert.Equal(ExecutionResultReasons.BranchNotSelected, received.ReasonCode);
            }
            else
            {
                Assert.Equal(EOutcome.Succeeded, received.Outcome);
            }
        }
    }

    /// <summary>全候補が非選択の通常合流は、別イベント待ちへ読み替えません。</summary>
    /// <returns>未到着と確定した非選択を分けた後続結果です。</returns>
    [Fact]
    public async Task AllExcludedMergeInputsDoNotStayPendingAsync()
    {
        ConditionalInputBranchTests.Sink sink = new(true);
        WorkflowFixture fixture = ConditionalInputBranchTests.Create(sink, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition original = ConditionalInputBranchTests.Definition(true, false);
        WorkflowDefinition definition = original with
        {
            Nodes = [.. original.Nodes, WorkflowFixture.Definition("merge", ValueMergePorts.NodeType)],
            Wires = [.. original.Wires.Select(wire => wire.FromNode switch { "concat" => wire with { ToNode = "merge", ToPort = "input1" }, _ => wire }),
                new("merge", "value", "sink", "$settings.remoteAddress")],
        };
        ExecutionReport result = await ConditionalInputBranchTests.RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, sink.Calls);
        Assert.Equal(ExecutionResultReasons.BranchNotSelected, ConditionalInputBranchTests.Result(result, "sink").ReasonCode);
    }

    /// <summary>開始信号を通常の真偽値として公開するレシピ境界です。</summary>
    /// <returns>省略可能ではない公開ポートです。</returns>
    private static NodeDefinition Field(string id, bool output, string name)
    {
        string type = SubworkflowInterface.InputType;
        if (output)
        {
            type = SubworkflowInterface.OutputType;
        }
        return new(id, type, JsonSerializer.SerializeToElement(new { name, valueType = new { type = "boolean" } }));
    }
}
