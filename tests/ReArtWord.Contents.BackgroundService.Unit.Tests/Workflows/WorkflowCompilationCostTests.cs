using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>グラフ全体の推論索引を接続ごとに再構築せず、検証結果を維持することを確認します。</summary>
public sealed class WorkflowCompilationCostTests(ITestOutputHelper output)
{
    /// <summary>検査用索引の準備で不正ノードを例外化せず、既存の検証結果を返します。</summary>
    /// <returns>保存を拒否する診断です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task InvalidNodeTypeRemainsAValidationIssueAsync(string? type)
    {
        WorkflowFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition node = new("invalid", type!, JsonSerializer.SerializeToElement(new { }));
        CompilationResult result = fixture.Compiler.Compile(new("invalid-workflow", "Invalid workflow", [node], []));
        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => issue.Code is "node.required");
    }

    /// <summary>同じノード数・配線のコンパイル時間と割当量を継続計測します。</summary>
    /// <returns>保存時の検証に必要なコストです。</returns>
    [Theory]
    [InlineData(96)]
    [InlineData(384)]
    public async Task MeasuresConnectedGraphCompilationAsync(int count)
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition[] nodes = Enumerable.Range(0, count).Select(index => WorkflowFixture.Definition("node-" + index)).ToArray();
        WireDefinition[] wires = Enumerable.Range(1, count - 1).Select(index => new WireDefinition(nodes[index - 1].Id, "value", nodes[index].Id, "value")).ToArray();
        WorkflowDefinition definition = new("compile-cost", "Compile cost", nodes, wires);
        Assert.NotNull(fixture.Compiler.Compile(definition).Workflow);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        CompilationResult result = fixture.Compiler.Compile(definition);
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(result.Workflow);
        Assert.Equal(count, result.Workflow.OrderedNodes.Count);
        output.WriteLine(JsonSerializer.Serialize(new { nodes = count, wires = wires.Length, milliseconds, allocatedBytes }));
        Assert.InRange(allocatedBytes, 0, count * 32768L);
    }
}
