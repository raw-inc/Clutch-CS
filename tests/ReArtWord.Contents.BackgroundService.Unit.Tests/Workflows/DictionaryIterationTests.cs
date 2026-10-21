using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>Key-Value結果だけを辞書へ収集し、他の結果列や通常Object配列と区別します。</summary>
public sealed class DictionaryIterationTests
{
    private static readonly string[] SingleKey = ["a"];

    /// <summary>既存のEnd追加結果からCommentを経由する抽選経路をそのまま実行できます。</summary>
    /// <returns>型検証と後続への一組の伝達の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRandomPickAfterKeyValueResultsRemainsExecutableAsync(bool parallel)
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new KeyValueNode(), new CommentNode(), new RandomPickNode(), new KeyValuePartNode(false));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string loopType = parallel ? "control.parallelForEach"
                                   : "control.forEach";
        WorkflowDefinition definition = new("existing", "Existing", [
            new("each", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(SingleKey) }),
            new("pair", "value.keyValue", empty), new("end", IterationNodeTypes.End, empty),
            new("comment", "value.comment", empty), new("random", "data.randomPick", empty), new("value", "data.keyValue.value", empty)],
            [new("each", "item", "pair", "key"), new("each", "index", "pair", "value"), new("each", "loop", "end", "loop"),
                new("each", "index", "end", "value"), new("pair", "value", "end", "value2"), new("end", "value2", "comment", "value"),
                new("comment", "value", "random", "value"), new("random", "value", "value", "value")]);
        Assert.All(definition.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(definition, fixture.Registry.Descriptors, wire)));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        NodeValue result = report.Nodes.Single(node => node.NodeId is "random").Targets.Single().Result.Outputs["value"];
        Assert.Equal(0, result.Json.GetProperty("a").GetInt32());
        Assert.Equal(0, report.Nodes.Single(node => node.NodeId is "value").Targets.Single().Result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>逐次・並列の結果列でDictionaryとArrayを独立して選択します。</summary>
    /// <returns>キーの対応、重複方針、空辞書と表示型の確認です。</returns>
    [Theory]
    [InlineData(false, false, false, 3)]
    [InlineData(true, false, false, 3)]
    [InlineData(true, true, false, 3)]
    [InlineData(true, true, true, 3)]
    [InlineData(false, false, false, 0)]
    public async Task KeyValueResultsAreCollectedAsDictionariesAsync(bool parallel, bool duplicate, bool replace, int count)
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new KeyValueNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string loopType = "control.forEach";
        if (parallel)
        {
            loopType = "control.parallelForEach";
        }
        string[] keys = Enumerable.Range(0, count).Select(index => "key-" + index).ToArray();
        if (duplicate)
        {
            keys[2] = keys[0];
        }
        string policy = "reject";
        if (replace)
        {
            policy = "replace";
        }
        WorkflowDefinition definition = new("collect", "Collect", [
            new("each", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(keys) }),
            new("pair", "value.keyValue", empty), new("end", IterationNodeTypes.End, JsonSerializer.SerializeToElement(new { duplicate = policy }))],
            [new("each", "item", "pair", "key"), new("each", "index", "pair", "value"), new("each", "loop", "end", "loop"),
                new("pair", "value", "end", "value"), new("each", "index", "end", "value2")]);
        WorkflowTypeInference inference = new(definition, fixture.Registry.Descriptors);
        Assert.Equal("integer", inference.PortType("end", "value", true)!.ValueType!.Type);
        Assert.Equal("array", inference.PortType("end", "value2", true)!.Type);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        NodeResult result = report.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result;
        if (duplicate && !replace)
        {
            Assert.Equal(EOutcome.Failed, report.Outcome);
            Assert.Equal("iteration.key.duplicate", result.ReasonCode);
            Assert.Empty(result.Outputs);
            return;
        }
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(keys.Distinct(StringComparer.Ordinal).Count(), result.Outputs["value"].Json.EnumerateObject().Count());
        Assert.Equal(Enumerable.Range(0, count), result.Outputs["value2"].Json.EnumerateArray().Select(value => value.GetInt32()));
        if (count != 0)
        {
            Assert.Equal(2, result.Outputs["value"].Json.GetProperty(keys[2]).GetInt32());
        }
    }

    /// <summary>普通のObject配列は辞書と取り違えず、元の配列として収集します。</summary>
    /// <returns>既存オブジェクト反復の互換性です。</returns>
    [Fact]
    public async Task PlainObjectResultsRemainAnArrayAsync()
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachEndNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("plain", "Plain", [
            new("each", "control.forEach", empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new[] { new { A = 1 }, new { A = 2 } }) }),
            new("end", IterationNodeTypes.End, empty)], [new("each", "loop", "end", "loop"), new("each", "item", "end", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(2, report.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"].Json.GetArrayLength());
    }

    /// <summary>辞書反復の一組を二つの抽出ノードへ渡し、キー型と値型を保持します。</summary>
    /// <returns>整数キーとPathを含む抽出の確認です。</returns>
    [Fact]
    public async Task KeyAndValueCanBeExtractedWithoutLosingTypesAsync()
    {
        NodeValue pair = PathTypeMetadata.Apply(NodeValue.FromObject(new Dictionary<string, string> { ["1"] = "folder/file.png" }), new("object", KeyType: new("integer"), ValueType: new("path")));
        KeyValuePartNode key = new(true), value = new(false);
        NodeResult extractedKey = await key.ExecuteAsync(Data.NodeTestContext.Create(value: pair)).ConfigureAwait(true);
        NodeResult extractedValue = await value.ExecuteAsync(Data.NodeTestContext.Create(value: pair)).ConfigureAwait(true);
        Assert.Equal(1, extractedKey.Outputs["value"].Json.GetInt32());
        Assert.Equal("path", extractedValue.Outputs["value"].DataType!.Type);
        foreach (object input in new object[] { new { }, new { A = 1, B = 2 }, new[] { 1, 2 } })
        {
            Assert.Equal("value.keyValue.singleEntry", (await key.ExecuteAsync(Data.NodeTestContext.Create(value: NodeValue.FromObject(input))).ConfigureAwait(true)).ReasonCode);
        }
    }
}
