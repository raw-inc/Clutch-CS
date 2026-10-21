using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>反復の要素化と集約を通っても、Pathの結合規則を維持します。</summary>
public sealed class PathIterationTests
{
    private static readonly string[] paths = [@"root\first", @"root\second"];
    private static readonly string[] invalidPaths = ["root/valid", "root/bad*name"];

    /// <summary>外部から型情報だけを付けた不正Path配列も、要素を開始する前に拒否します。</summary>
    /// <returns>途中まで実行しないことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAnnotatedArrayDoesNotStartAnIteration(bool parallel)
    {
        NodeValue input = NodeValue.FromObject(paths) with { DataType = new("array", new("path")) };
        input = input with { Json = JsonSerializer.SerializeToElement(invalidPaths) };
        NodeResult result = await new ForEachNode(parallel).ExecuteAsync(NodeTestContext.Create(value: input)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>自動・手動・スキーマの各方式と、逐次・並列反復で型を引き継ぎます。</summary>
    /// <returns>各要素のPath結合と入力順の集約です。</returns>
    [Theory]
    [InlineData("auto", false)]
    [InlineData("auto", true)]
    [InlineData("manual", false)]
    [InlineData("manual", true)]
    [InlineData("schema", false)]
    [InlineData("schema", true)]
    public async Task ElementsAndCollectedValuesKeepPathSemantics(string mode, bool parallel)
    {
        ForEachNode each = new(parallel);
        ForEachEndNode end = new();
        WorkflowFixture fixture = new(each, end, new ConcatNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeValue values = NodeValue.FromObject(paths);
        string automatic = "auto";
        if (string.Equals(mode, automatic, StringComparison.Ordinal))
        {
            values = PathTypeMetadata.Apply(values, new("array", new("path")));
        }
        NodeDefinition start = new("each", each.Descriptor.Type, JsonSerializer.SerializeToElement(new
        {
            elementMode = mode, elementType = new { type = "path" }, elementSchema = new { type = "string", format = "path" }
        }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = values });
        NodeDefinition concat = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input2"] = PathValues.Create("clip.wav") });
        NodeDefinition finish = new("end", end.Descriptor.Type, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition definition = new("path-loop", "Path loop", [start, concat, finish],
            [new("each", "item", "concat", "input1"), new("each", "loop", "end", "loop"), new("concat", "value", "end", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport result = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        NodeValue collected = Assert.Single(Assert.Single(result.Nodes, node => node.NodeId is "end").Targets).Result.Outputs["value"];
        Assert.Equal(["root/first/clip.wav", "root/second/clip.wav"], collected.Json.EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("path", collected.DataType!.ElementType!.Type);
    }
}
