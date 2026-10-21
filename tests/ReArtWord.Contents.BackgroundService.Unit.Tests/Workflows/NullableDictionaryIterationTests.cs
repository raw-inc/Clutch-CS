using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>片方の辞書値だけがnullでも、同じ要素の二つの収集結果を後続へ渡します。</summary>
public sealed class NullableDictionaryIterationTests
{
    /// <summary>逐次・並列反復で、空の側を含む二列のキー対応とnullを保持します。</summary>
    /// <returns>スキーマ解析からKey-Value、End、後続への受け渡しの確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullableColumnsReachBothDownstreamNodesAsync(bool parallel, bool mixed)
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new SerializationNode("json", false), new KeyValueNode(), new ConvertNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        string?[] first = mixed ? [null, "first-b", null]
                                : [null, null, null];
        string?[] second = mixed ? ["second-a", null, null]
                                 : ["second-a", "second-b", "second-c"];
        string[] values = Enumerable.Range(0, first.Length).Select(index => JsonSerializer.Serialize(new { first = first[index], second = second[index] })).ToArray();
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {"valueMode":"schema","valueSchema":{"type":"object","additionalProperties":false,"required":["first","second"],
            "properties":{"first":{"type":["string","null"]},"second":{"type":["string","null"]}}}}
            """);
        string loopType = parallel ? IterationNodeTypes.ParallelForEach
                                   : IterationNodeTypes.ForEach;
        WorkflowDefinition definition = new("nullable-columns", "Nullable columns", [
            new("each", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(values) }),
            new("parse", "data.json.parse", schema), new("key", "data.convert", JsonSerializer.SerializeToElement(new { targetType = "string" })),
            new("first", "value.keyValue", empty), new("second", "value.keyValue", empty), new("end", IterationNodeTypes.End, empty),
            new("first-result", "control.pass", empty), new("second-result", "control.pass", empty)],
            [new("each", "item", "parse", "value"), new("each", "index", "key", "value"),
                new("key", "value", "first", "key"), new("key", "value", "second", "key"),
                new("parse", "value/first", "first", "value"), new("parse", "value/second", "second", "value"),
                new("each", "loop", "end", "loop"), new("first", "value", "end", "value"), new("second", "value", "end", "value2"),
                new("end", "value", "first-result", "value"), new("end", "value2", "second-result", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, IterationTests.Loop(report).Iteration!.Started);
        foreach ((string node, string?[] expected) in new[] { ("first-result", first), ("second-result", second) })
        {
            NodeValue result = Assert.Single(report.Nodes.Single(item => string.Equals(item.NodeId, node, StringComparison.Ordinal)).Targets).Result.Outputs["value"];
            Assert.NotNull(result.DataType?.KeyType);
            Assert.Equal(expected.Length, result.Json.EnumerateObject().Count());
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index], result.Json.GetProperty(index.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString());
            }
        }
    }
}
