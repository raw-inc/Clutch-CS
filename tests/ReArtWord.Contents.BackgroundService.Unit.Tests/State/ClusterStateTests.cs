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
    /// <summary>共有キーは実行PCの時計に依存せず、元のTTLの間は重複となり、実際の期限後に再取得できます。</summary>
    /// <returns>二つのCoreの実通信と永続状態での確認です。</returns>
    [Fact]
    public async Task SharedFiniteDedupeUsesTheOwnerClock()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        NodeStateStore first = new(fixture.Repositories["a"], "a"), second = new(fixture.Repositories["b"], "b");
        using StateReplication a = new(fixture.Peers["a"], fixture.Repositories["a"], first);
        using StateReplication b = new(fixture.Peers["b"], fixture.Repositories["b"], second);
        StateNode left = new("dedupe", first), right = new("dedupe", second);
        ManagementTransport.AdmissionTestClock clock = new();
        NodeExecutionContext context = NodeTestContext.Create("""{"scope":"workflow","key":"ttl","ttlMilliseconds":2000}""", NodeValue.FromObject(42), time: clock);
        Assert.Equal("success", (await left.ExecuteAsync(context)).Route);
        clock.Adjust(TimeSpan.FromDays(1));
        Assert.Equal("duplicate", (await right.ExecuteAsync(context)).Route);
        await Task.Delay(TimeSpan.FromMilliseconds(2000));
        clock.Adjust(TimeSpan.FromDays(-2));
        Assert.Equal("success", (await right.ExecuteAsync(context)).Route);
        Assert.Equal("duplicate", (await left.ExecuteAsync(context)).Route);
    }

    /// <summary>明示離脱中に初めて見たキーは、その側で一度だけ確保し、ローカル限定であることを返します。</summary>
    /// <returns>通常の共有状態に書き込まない離脱側の結果です。</returns>
    [Fact]
    public async Task DetachedFiniteReservationStaysLocal()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true);
        NodeStateStore store = new(fixture.Repositories["a"], "a");
        using StateReplication replication = new(fixture.Peers["a"], fixture.Repositories["a"], store);
        await fixture.Peers["a"].DetachAsync();
        string scope = NodeStateScope.Shared("detached");
        StateWriteResult first = await store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1));
        Assert.True(first.Applied);
        Assert.True(first.LocalOnly);
        StateWriteResult duplicate = await store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1));
        Assert.False(duplicate.Applied);
        Assert.True(duplicate.LocalOnly);
        Assert.Null(await fixture.Repositories["a"].ReadAsync(scope, "event"));
    }

    /// <summary>旧形式の日時だけでキーを解放せず、保存版を確認して元TTLが経てば再取得できます。</summary>
    /// <returns>旧マーカーの移行と、同時取得時の成功・重複の区別です。</returns>
    [Fact]
    public async Task LegacyMarkerBecomesReusableAfterAProvenUpperBound()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true);
        NodeStateStore store = new(fixture.Repositories["a"], "a");
        using StateReplication replication = new(fixture.Peers["a"], fixture.Repositories["a"], store);
        string scope = NodeStateScope.Shared("legacy");
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-1);
        TimeSpan ttl = TimeSpan.FromMilliseconds(300);
        Assert.True((await store.CompareExchangeAsync(scope, "event", null, NodeValue.FromObject(new { reservedAt = old, expiresAt = old + ttl }))).Applied);
        StateWriteResult uncertain = await store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1));
        Assert.Equal(EOutcome.Unknown, uncertain.Outcome);
        Assert.False(uncertain.Applied);
        await Task.Delay(ttl);
        StateWriteResult[] results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1)).AsTask()));
        Assert.All(results, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.Single(results, result => result.Applied);
    }

    /// <summary>通信を分けた各側で新規の同じキーを確保し、再接続時は一方の値を日時で捨てません。</summary>
    /// <returns>分断中の実行と、同期後の二つの保存候補です。</returns>
    [Fact]
    public async Task PartitionsReserveIndependentlyAndKeepBothMarkersAfterRejoin()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        await fixture.StopAsync("b");
        using System.Net.Sockets.UdpClient port = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int partition = ((System.Net.IPEndPoint)port.Client.LocalEndPoint!).Port;
        port.Close();
        await fixture.StartAsync("b", networkPort: partition);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Ready) == 1));
        string scope = NodeStateScope.Shared("partition");
        foreach (string peer in new[] { "a", "b" })
        {
            NodeStateStore store = new(fixture.Repositories[peer], peer);
            using StateReplication replication = new(fixture.Peers[peer], fixture.Repositories[peer], store);
            StateWriteResult first = await store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1));
            Assert.True(first.Applied, first.Reason);
            Assert.False((await store.ReserveAsync(scope, "event", TimeSpan.FromMinutes(1))).Applied);
        }
        await fixture.StopAsync("b");
        await fixture.StartAsync("b");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string key = "event";
        Infrastructure.Persistence.IDurableRepository rejoined = fixture.Repositories["a"];
        while ((await rejoined.ReadWithConflictsAsync(scope, key, timeout.Token)).Conflicts.Count < 2)
        {
            await Task.Delay(50, timeout.Token);
        }
        IReadOnlyList<Infrastructure.Persistence.DurableEntry> candidates = (await fixture.Repositories["a"].ReadWithConflictsAsync(scope, "event", timeout.Token)).Conflicts;
        Assert.Equal(2, candidates.Select(candidate => candidate.Version).Distinct().Count());
    }

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
