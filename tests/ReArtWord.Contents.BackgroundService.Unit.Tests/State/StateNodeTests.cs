using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>比較更新と重複抑止の観測結果を検証します。</summary>
public sealed class StateNodeTests
{
    /// <summary>並行した同一キーを一度だけ新規通過させます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ConcurrentDedupePassesExactlyOneInput()
    {
        TestStateStore store = new();
        StateNode node = new("dedupe", store);
        Task<NodeResult>[] tasks = Enumerable.Range(0, 20).Select(index => Task.Run(() => node.ExecuteAsync(NodeTestContext.Create("""{"scope":"workflow","key":"event"}""", NodeValue.FromObject(index))).AsTask())).ToArray();
        NodeResult[] results = await Task.WhenAll(tasks);
        Assert.Single(results, result => string.Equals(result.Route, "success", StringComparison.Ordinal));
        Assert.Equal(19, results.Count(result => string.Equals(result.Route, "duplicate", StringComparison.Ordinal)));
        NodeResult applied = Assert.Single(results, result => result.Outputs["applied"].Json.GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Number, applied.Outputs["value"].Json.ValueKind);
        Assert.True(applied.Outputs["exists"].Json.GetBoolean());
    }

    /// <summary>明示したPeer範囲は同一キーでも独立しています。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task PeerScopeAndWorkflowScopeRemainDistinct()
    {
        TestStateStore store = new();
        StateNode node = new("dedupe", store);
        NodeExecutionContext first = NodeTestContext.Create("""{"scope":"peer","key":"event"}""", NodeValue.Null);
        Assert.Equal("success", (await node.ExecuteAsync(first)).Route);
        Assert.Equal("success", (await node.ExecuteAsync(first with { PeerId = "other" })).Route);
        Assert.Equal("duplicate", (await node.ExecuteAsync(first)).Route);
    }

    /// <summary>比較不成立で状態を上書きしません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CompareExchangeReportsConflictAndKeepsValue()
    {
        TestStateStore store = new();
        NodeExecutionContext context = NodeTestContext.Create("""{"scope":"workflow","key":"counter"}""", NodeValue.FromObject(1));
        NodeResult created = await new StateNode("set", store).ExecuteAsync(context);
        Assert.Equal("success", created.Route);
        NodeResult conflict = await new StateNode("compareExchange", store).ExecuteAsync(context with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(9) } });
        Assert.Equal("conflict", conflict.Route);
        Assert.Equal(1, conflict.Outputs["value"].Json.GetInt32());
    }
}
