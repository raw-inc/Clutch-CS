using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>離脱側のstate/dedupeを共有正本から分離し、元版から再参加統合します。</summary>
public sealed class DetachedStateTests
{
    /// <summary>未変更側は共有確認後に統合し、両側更新は候補を保持して明示解消します。</summary>
    /// <returns>実UDP二PeerのローカルCASと再参加後の結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedWorkflowStateIsLocalUntilRejoin(bool concurrent)
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        NodeStateStore first = new(fixture.Repositories["a"], "a");
        NodeStateStore second = new(fixture.Repositories["b"], "b");
        using StateReplication firstReplication = new(fixture.Peers["a"], fixture.Repositories["a"], first);
        using StateReplication secondReplication = new(fixture.Peers["b"], fixture.Repositories["b"], second);
        using ClusterConfigurationStore firstCommon = new(fixture.Repositories["a"], fixture.Peers["a"]);
        using ClusterConfigurationStore secondCommon = new(fixture.Repositories["b"], fixture.Peers["b"]);
        using ClusterConflictService firstConflicts = Conflicts(fixture, "a", first, firstCommon);
        using ClusterConflictService secondConflicts = Conflicts(fixture, "b", second, secondCommon);
        string scope = "workflow:demo";
        StateWriteResult initial = await first.CompareExchangeAsync(scope, "key", null, NodeValue.FromObject(1));
        Assert.True(initial.Applied);
        await fixture.Peers["a"].DetachAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Any(peer => peer.Status == EPeerStatus.Detached));
        StateWriteResult local = await first.CompareExchangeAsync(scope, "key", initial.Current!.Version, NodeValue.FromObject(2));
        Assert.True(local.Applied);
        Assert.True(local.LocalOnly);
        Assert.True(local.Current!.LocalOnly);
        Assert.Equal(1, (await fixture.Repositories["a"].ReadAsync(scope, "key"))!.Value!.Json.GetInt32());
        Assert.Equal(1, (await second.ReadAsync(scope, "key"))!.Value.Json.GetInt32());
        if (concurrent)
        {
            Assert.True((await second.CompareExchangeAsync(scope, "key", initial.Current.Version, NodeValue.FromObject(3))).Applied);
        }
        await fixture.Peers["a"].RejoinAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        await first.ReconcileDetachedAsync();
        if (concurrent)
        {
            await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Degraded) == 2));
            StateConflict conflict = Assert.Single(await firstConflicts.GetAsync());
            NodeDefinition stateNode = new("state", "state.get", JsonSerializer.SerializeToElement(new { scope = "workflow", key = "key" }));
            Assert.NotNull(firstConflicts.GetExecutionProblem("demo", stateNode));
            string selected = conflict.Candidates.Single(candidate => candidate.Value!.Json.GetInt32() == 3).Version;
            Assert.Equal(EOutcome.Succeeded, (await firstConflicts.ResolveAsync(new(Guid.NewGuid(), conflict.Id, conflict.Candidates.Select(candidate => candidate.Version).ToArray(), selected))).Shared.Outcome);
        }
        await first.ReconcileDetachedAsync();
        Assert.False((await first.ReadAsync(scope, "key"))!.LocalOnly);
        int expected = 2;
        if (concurrent)
        {
            expected = 3;
        }
        Assert.Equal(expected, (await second.ReadAsync(scope, "key"))!.Value.Json.GetInt32());
    }

    /// <summary>実状態用途の依存分類を各Peerへ登録します。</summary>
    /// <returns>試験で所有する競合サービスです。</returns>
    private static ClusterConflictService Conflicts(ManualSafetyFixture fixture, string id, NodeStateStore states, ClusterConfigurationStore common)
    {
        NodeRegistry registry = new(StateNodeRegistration.Create(states));
        WorkflowStore workflows = new(fixture.Repositories[id], fixture.Peers[id], new WorkflowCompiler(registry));
        ClusterConflictService conflicts = new(fixture.Repositories[id], fixture.Peers[id], workflows, common, registry);
        fixture.Peers[id].SetConflictReadinessCheck(conflicts.PrepareAsync);
        return conflicts;
    }
}
