using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>開始信号とデータ入力を分け、非選択枝の副作用を開始しないことを確認します。</summary>
public sealed class IfStartTests
{
    /// <summary>双方の通過条件と不正な条件を、値の暗黙変換なしで判定します。</summary>
    /// <returns>通過時だけ開始信号が存在することの確認です。</returns>
    [Theory]
    [InlineData(false, "true", EOutcome.Succeeded)]
    [InlineData(false, "false", EOutcome.NotExecuted)]
    [InlineData(true, "false", EOutcome.Succeeded)]
    [InlineData(true, "true", EOutcome.NotExecuted)]
    [InlineData(false, "\"true\"", EOutcome.Failed)]
    [InlineData(true, "0", EOutcome.Failed)]
    [InlineData(false, "null", EOutcome.Failed)]
    public async Task OnlyMatchingBooleansEmitSignalsAsync(bool inverted, string json, EOutcome expected)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["condition"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json)) };
        NodeResult result = await new IfNode(inverted).ExecuteAsync(NodeTestContext.Create(extra: inputs));
        Assert.Equal(expected, result.Outcome);
        if (expected == EOutcome.Succeeded)
        {
            Assert.True(result.Outputs["start"].Json.GetBoolean());
            Assert.Single(result.Outputs);
        }
        else
        {
            Assert.Empty(result.Outputs);
        }
    }

    /// <summary>データが届いていても、非選択の開始信号を迂回できません。</summary>
    /// <returns>反復中に必要な副作用だけが一回ずつ実行されることの確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IterationDataCannotBypassTheStartGateAsync(bool inverted, bool parallel)
    {
        ConcurrentQueue<int> calls = [];
        WorkflowFixture.ActionNode action = new((context, _) =>
        {
            calls.Enqueue(context.Inputs["value"].Json.GetInt32());
            return ValueTask.FromResult(NodeResult.Success());
        }, required: true);
        IfNode gate = new(inverted);
        ForEachNode each = new(parallel);
        WorkflowFixture fixture = new(each, gate, action);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition source = new("each", each.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(new[] { new { enabled = false, id = 1 }, new { enabled = true, id = 2 }, new { enabled = false, id = 3 }, new { enabled = true, id = 4 } })
        });
        NodeDefinition condition = new("gate", gate.Descriptor.Type, JsonSerializer.SerializeToElement(new { }));
        NodeDefinition target = new("target", action.Descriptor.Type, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("flow", "flow", [source, condition, target], [new("each", "item/enabled", "gate", "condition"), new("each", "item/id", "target", "value"), new("gate", "start", "target", "trigger")]);
        CompilationResult compiled = fixture.Compiler.Compile(flow);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        int[] expected = inverted ? [1, 3]
                                  : [2, 4];
        Assert.Equal(expected, calls.Order().ToArray());
    }
}
