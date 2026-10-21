using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterConflicts;

/// <summary>実UDPで内容分岐だけを隔離し、独立した実処理を維持します。</summary>
public sealed class ClusterConflictTests
{
    /// <summary>両側が異なる定義を保存した後、Aを止めBを続け、全候補確認で復旧します。</summary>
    /// <returns>分断・両候補共有・範囲隔離・明示解消の確認です。</returns>
    [Fact]
    public async Task DivergentWorkflowDoesNotStopIndependentWorkflow()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        WorkflowDefinition original = new("changed", "base", [], []);
        StoredWorkflow initial = Stored(original);
        DurableReceipt saved = await fixture.Peers["a"].ReplicateAsync(Guid.NewGuid(), [new("workflows", original.Id, null, NodeValue.FromObject(initial))]);
        Assert.Equal(EOutcome.Succeeded, saved.Outcome);
        await fixture.StopAsync("b");
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int partition = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        await fixture.StartAsync("b", networkPort: partition);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Ready) == 1));
        foreach (string id in new[] { "a", "b" })
        {
            StoredWorkflow changed = Stored(original with { Name = id });
            Assert.Equal(EOutcome.Succeeded, (await fixture.Peers[id].ReplicateAsync(Guid.NewGuid(), [new("workflows", original.Id, saved.Entries[0].Version, NodeValue.FromObject(changed))])).Outcome);
        }
        await fixture.StopAsync("b");
        await fixture.StartAsync("b");
        ConflictRuntime first = new(fixture, "a");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        ConflictRuntime second = new(fixture, "b");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Degraded) == 2));
        Assert.Single(await first.Conflicts.GetAsync());
        Assert.Equal(EOutcome.NotExecuted, (await first.ExecuteAsync("changed")).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.ExecuteAsync("independent")).Outcome);
        StateConflict conflict = Assert.Single(await first.Conflicts.GetAsync());
        ConflictResolveRequest request = new(Guid.NewGuid(), conflict.Id, conflict.Candidates.Select(item => item.Version).ToArray(), conflict.Candidates[0].Version);
        ConflictResolveResult result = await first.Conflicts.ResolveAsync(request);
        Assert.Equal(EOutcome.Succeeded, result.Shared.Outcome);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Ready) == 2));
        Assert.Empty(await second.Conflicts.GetAsync());
        Assert.Equal(EOutcome.Succeeded, (await first.ExecuteAsync("changed")).Outcome);
        Assert.Equal(ContractJson.Hash(result), ContractJson.Hash(await first.Conflicts.ResolveAsync(request)));
        ConflictResolveResult rejected = await first.Conflicts.ResolveAsync(request with { SelectedVersion = "different" });
        Assert.Equal(EOutcome.NotExecuted, rejected.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, rejected.Snapshot!.Outcome);
    }

    /// <summary>独立したノード編集は共通祖先から自動統合し、任意の時刻順で上書きしません。</summary>
    /// <returns>既存比較mergeを使う非競合統合です。</returns>
    [Fact]
    public async Task NonConflictingNodeEditsMergeFromActualCommonAncestor()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-auto-fork-" + Guid.NewGuid().ToString("N"));
        SqliteRepository first = new(Path.Combine(root, "a.db"));
        SqliteRepository second = new(Path.Combine(root, "b.db"));
        NodeDefinition one = new("one", "test.action", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition definition = new("demo", "original", [one, one with { Id = "two" }], []);
        DurableReceipt baseline = await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "demo", null, NodeValue.FromObject(Stored(definition)))]);
        await second.ImportAsync(await first.ExportAsync());
        WorkflowDefinition left = definition with { Nodes = [one with { Position = new NodePosition(1, 2) }, definition.Nodes[1]] };
        WorkflowDefinition right = definition with { Nodes = [one, definition.Nodes[1] with { Position = new NodePosition(3, 4) }] };
        await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "demo", baseline.Entries[0].Version, NodeValue.FromObject(Stored(left)))]);
        await second.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "demo", baseline.Entries[0].Version, NodeValue.FromObject(Stored(right)))]);
        await first.ImportAsync(await second.ExportAsync());
        (bool safe, NodeValue? value) = await ConflictAutoMerge.CreateAsync(await first.GetConflictsAsync(string.Empty), first);
        Assert.True(safe);
        StoredWorkflow merged = value!.Json.Deserialize<StoredWorkflow>()!;
        Assert.Equal(new NodePosition(1, 2), merged.Definition.Nodes[0].Position);
        Assert.Equal(new NodePosition(3, 4), merged.Definition.Nodes[1].Position);
    }

    /// <summary>内容に一致する定義の保存用データを作ります。</summary>
    /// <returns>設定内容を検証できる候補です。</returns>
    private static StoredWorkflow Stored(WorkflowDefinition definition) => new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);

    /// <summary>同じPeerへ実NodeExecutorと内容依存保護条件を登録します。</summary>
    private sealed class ConflictRuntime : IAsyncDisposable
    {
        private readonly ManualSafetyFixture fixture;
        private readonly string id;
        private readonly SourceSupervisor sources;
        private readonly ClusterConfigurationStore operational;

        /// <summary>既存実UDP PeerへCoreと同じ判定を接続します。</summary>
        internal ConflictRuntime(ManualSafetyFixture fixture, string id)
        {
            this.fixture = fixture;
            this.id = id;
            NodeRegistry registry = new([new WorkflowFixture.ActionNode((context, token) => ValueTask.FromResult(NodeResult.Success()))]);
            WorkflowEngine engine = new(registry, fixture.Peers[id], fixture.Peers[id]);
            NodeSafetyGate safety = new(fixture.Peers[id]);
            sources = new(registry, fixture.Peers[id], engine, safety);
            WorkflowStore workflows = new(fixture.Repositories[id], fixture.Peers[id], new WorkflowCompiler(registry));
            operational = new(fixture.Repositories[id], fixture.Peers[id]);
            Conflicts = new(fixture.Repositories[id], fixture.Peers[id], workflows, operational, registry);
            NodeExecutor executor = new(registry, fixture.Peers[id], sources, safety) { DependencyCheck = Conflicts.GetExecutionProblem };
            fixture.Peers[id].RemoteExecutionHandler = executor.ExecuteAsync;
            fixture.Peers[id].SetConflictReadinessCheck(Conflicts.PrepareAsync);
        }

        /// <summary>試験する内容競合サービスです。</summary>
        internal ClusterConflictService Conflicts
        {
            get;
        }

        /// <summary>新しい一回を実操作記録経路で実行します。</summary>
        /// <returns>依存保護条件後の実行結果です。</returns>
        internal Task<NodeResult> ExecuteAsync(string workflowId)
        {
            NodeDefinition node = new("node", "test.action", JsonSerializer.SerializeToElement(new { }));
            return fixture.Peers[id].ExecuteAsync(new(Guid.NewGuid(), Guid.NewGuid(), workflowId, "revision", node, new Dictionary<string, NodeValue>(), id,
                fixture.Peers[id].LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        /// <summary>試験所有の待受・排他を終了します。</summary>
        /// <returns>終了確認です。</returns>
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)sources).DisposeAsync().ConfigureAwait(false);
            Conflicts.Dispose();
            operational.Dispose();
        }
    }
}
