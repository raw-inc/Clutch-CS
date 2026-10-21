using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>一つのIfで二分岐を排他的に起動し、真偽値は通常データとして渡します。</summary>
public sealed class IfElseTests
{
    private static readonly bool[] conditions = [true, false, true, false];

    /// <summary>false側を選んだ場合も、開始信号はtrueで、比較値はfalseです。</summary>
    /// <returns>選択側だけに信号が存在することの確認です。</returns>
    [Theory]
    [InlineData(true, "trueStart", "falseStart")]
    [InlineData(false, "falseStart", "trueStart")]
    public async Task EmitsOnlySelectedSignalAndOriginalBooleanAsync(bool condition, string selected, string omitted)
    {
        IfElseNode node = new();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["condition"] = NodeValue.FromObject(condition) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Outputs.Count);
        Assert.True(result.Outputs[selected].Json.GetBoolean());
        Assert.False(result.Outputs.ContainsKey(omitted));
        Assert.Equal(condition, result.Outputs["value"].Json.GetBoolean());
        Assert.True(StartSignalPorts.IsOutput(node.Descriptor, selected));
        Assert.True(StartSignalPorts.IsOutput(node.Descriptor, omitted));
        Assert.False(StartSignalPorts.IsOutput(node.Descriptor, "value"));
        Assert.Equal("boolean", BuiltinOutputTypes.Get(node.Descriptor.Type, selected)!.Type);
    }

    /// <summary>真偽値以外を暗黙変換せず、どちらの枝にも信号を出しません。</summary>
    /// <returns>不正な条件を非選択扱いで隠さない確認です。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    public async Task RejectsNonBooleanConditionsAsync(string json)
    {
        NodeResult result = await new IfElseNode().ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue>
        {
            ["condition"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json)),
        })).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.NotNull(result.ReasonCode);
    }

    /// <summary>両枝に通常値が届いても、選択側だけを反復ごとに一度起動します。</summary>
    /// <returns>逐次・並列ForEachでの重複信号と値混線の防止です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IterationsDoNotBypassOrReuseBranchSignalsAsync(bool parallel)
    {
        ConcurrentQueue<(string Branch, int Index, bool Value)> calls = new();
        WorkflowFixture fixture = new(new ForEachNode(parallel), new IfElseNode(), new ValueMergeFixture.Probe((context, _) =>
        {
            calls.Enqueue((context.NodeId, context.Inputs["value"].Json.GetInt32(), context.Inputs["number"].Json.GetBoolean()));
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string type = parallel ? "control.parallelForEach"
                               : "control.forEach";
        WorkflowDefinition definition = new("if-branches", "If branches", [
            WorkflowFixture.Definition("loop", type) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(conditions) } },
            WorkflowFixture.Definition("if", "control.if"), WorkflowFixture.Definition("yes", "test.value"), WorkflowFixture.Definition("no", "test.value")],
            [new("loop", "item", "if", "condition"), new("if", "trueStart", "yes", "trigger"), new("if", "falseStart", "no", "trigger"),
                new("loop", "index", "yes", "value"), new("loop", "index", "no", "value"), new("if", "value", "yes", "number"), new("if", "value", "no", "number")]);
        Runtime.Workflows.Compilation.CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(new[] { ("yes", 0, true), ("no", 1, false), ("yes", 2, true), ("no", 3, false) }, calls.OrderBy(call => call.Index));
    }
}
