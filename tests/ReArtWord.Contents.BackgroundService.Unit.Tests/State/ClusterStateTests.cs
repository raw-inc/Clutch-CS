using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>クラスタ共通の永続値と、従来のWorkflow・Peer内の値を分離します。</summary>
public sealed class ClusterStateTests
{
    /// <summary>異なる実行・WFから同じクラスタ値を参照し、同名のWF Stateは変えません。</summary>
    /// <returns>通常set/get/deleteと実行単位非依存の確認です。</returns>
    [Fact]
    public async Task SharedAcrossWorkflowsButNotWorkflowScopeAsync()
    {
        TestStateStore store = new();
        NodeExecutionContext first = NodeTestContext.Create("""{"scope":"cluster","key":"cue"}""", NodeValue.FromObject(12));
        Assert.Equal(EOutcome.Succeeded, (await new StateNode("set", store).ExecuteAsync(first).ConfigureAwait(true)).Outcome);
        NodeExecutionContext second = first with { ExecutionId = Guid.NewGuid(), WorkflowId = "other", PeerId = "b" };
        NodeResult read = await new StateNode("get", store).ExecuteAsync(second).ConfigureAwait(true);
        Assert.Equal(12, read.Outputs["value"].Json.GetInt32());
        Assert.Null(await store.ReadAsync(NodeStateScope.Shared(first.WorkflowId), "cue").ConfigureAwait(true));
        Assert.Null(await store.ReadAsync(NodeStateScope.Peer("b", "other"), "cue").ConfigureAwait(true));
        Assert.True((await new StateNode("delete", store).ExecuteAsync(second).ConfigureAwait(true)).Outputs["applied"].Json.GetBoolean());
        Assert.False((await new StateNode("get", store).ExecuteAsync(first).ConfigureAwait(true)).Outputs["exists"].Json.GetBoolean());
        Assert.True(NodeStateScope.IsNodeScope(NodeStateScope.Cluster, "a"));
        Assert.False(NodeStateScope.TryGetWorkflowId(NodeStateScope.Cluster, "a", out _));
        Assert.False(NodeStateScope.IsNodeScope("cluster:operations", "a"));
    }

    /// <summary>実UDP複製後、別Peerの保存先を開き直して同じ永続値を読めます。</summary>
    /// <returns>比較保存、複製、保存先再構築の確認です。</returns>
    [Fact]
    public async Task ClusterStateReplicatesAndSurvivesStoreRecreationAsync()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        NodeStateStore first = new(fixture.Repositories["a"], "a");
        using StateReplication firstReplication = new(fixture.Peers["a"], fixture.Repositories["a"], first);
        StateWriteResult seeded = await first.CompareExchangeAsync(NodeStateScope.Cluster, "cue", null, NodeValue.FromObject(12)).ConfigureAwait(true);
        Assert.True(seeded.Applied);
        await fixture.StartAsync("b").ConfigureAwait(true);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2).ConfigureAwait(true);
        NodeStateStore second = new(fixture.Repositories["b"], "b");
        using StateReplication secondReplication = new(fixture.Peers["b"], fixture.Repositories["b"], second);
        Assert.Equal(12, (await second.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value.Json.GetInt32());
        StateWriteResult written = await second.CompareExchangeAsync(NodeStateScope.Cluster, "cue", seeded.Current!.Version, NodeValue.FromObject(23)).ConfigureAwait(true);
        Assert.True(written.Applied, written.Reason);
        Assert.Equal(23, (await first.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value.Json.GetInt32());
        NodeStateStore reopened = new(fixture.Repositories["b"], "b");
        Assert.Equal(23, (await reopened.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value.Json.GetInt32());
        Assert.False((await first.CompareExchangeAsync(NodeStateScope.Cluster, "cue", "stale", NodeValue.Null).ConfigureAwait(true)).Applied);
        StateWriteResult removed = await first.CompareExchangeAsync(NodeStateScope.Cluster, "cue", written.Current!.Version, null).ConfigureAwait(true);
        Assert.True(removed.Applied);
        Assert.Null(await second.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true));
    }
}
