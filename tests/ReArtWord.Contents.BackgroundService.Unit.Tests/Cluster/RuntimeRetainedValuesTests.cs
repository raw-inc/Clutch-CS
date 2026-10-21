using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>実行中の値共有を実通信で比較更新し、担当交代時に古い値を復活させません。</summary>
public sealed class RuntimeRetainedValuesTests
{
    /// <summary>厳密な旧Peer契約でも新Coreの生存通知を解釈でき、対応照会だけを別に扱います。</summary>
    [Fact]
    public void CapabilityDoesNotChangeTheLegacyAnnouncementShape()
    {
        PeerInfo current = new("a", Guid.NewGuid(), "host", "127.0.0.1", 12345, EPeerStatus.Ready, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BufferedRuntime: true);
        JsonElement wire = JsonSerializer.SerializeToElement(current);
        LegacyPeer restored = wire.Deserialize<LegacyPeer>(ContractJson.Options)!;
        LegacyPeer expected = new(current.PeerId, current.InstanceId, current.HostId, current.Address, current.Port, current.Status,
            current.Priority, current.ReadySince, current.LastSeen, current.LogPort, current.PersistenceStatus);
        Assert.Equal(expected, restored);
    }

    /// <summary>対応照会を持たないPeerが参加している間は、共有値を従来の保存先へ書きます。</summary>
    /// <returns>新旧混在時の実発見と、保存確認付きの互換動作です。</returns>
    [Fact]
    public async Task LegacyPeerKeepsTheDurableRetentionPath()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        MemoryRepository store = new();
        await using PeerCluster first = Create(store, id, "first", port, true);
        await using PeerCluster legacy = Create(new MemoryRepository(), id, "legacy", port, false);
        NodeStateStore durable = new(store, "first");
        using StateReplication replication = new(first, store, durable);
        RuntimeRetainedValues values = new(first, durable);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await first.StartAsync(timeout.Token);
        await legacy.StartAsync(timeout.Token);
        await ReadyAsync(first, legacy, timeout.Token);
        string scope = "workflow:mixed", key = "runtime:node-output:node";
        StateWriteResult result = await values.CompareExchangeAsync(scope, key, null, NodeValue.FromObject(8), timeout.Token);
        Assert.True(result.Applied, result.Reason);
        Assert.NotNull(await store.ReadAsync(scope, key, timeout.Token));
        Assert.False(Assert.Single(first.GetPeers(), peer => peer.PeerId is "legacy").BufferedRuntime);
    }

    /// <summary>別Peerからの参照でも最新世代だけを供給し、過去世代による上書きを拒否します。</summary>
    /// <returns>メモリ上の共有CASと二度の担当交代です。</returns>
    [Fact]
    public async Task SharedMemoryUsesCurrentCoordinatorAndNeverResurrectsAnOldGeneration()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        MemoryRepository firstStore = new(), secondStore = new();
        await using PeerCluster first = Create(firstStore, cluster, "first", port, true);
        await using PeerCluster second = Create(secondStore, cluster, "second", port, false);
        RuntimeRetainedValues firstValues = new(first, new NodeStateStore(firstStore, "first"));
        RuntimeRetainedValues secondValues = new(second, new NodeStateStore(secondStore, "second"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        await ReadyAsync(first, second, timeout.Token);
        string scope = "workflow:test", key = "runtime:node-output:test", firstPeer = "first", secondPeer = "second";
        StateWriteResult initial = await secondValues.CompareExchangeAsync(scope, key, null, NodeValue.FromObject(1), timeout.Token);
        Assert.True(initial.Applied, initial.Reason);
        Assert.Equal(1, (await firstValues.ReadAsync(scope, key, timeout.Token))!.Value.Json.GetInt32());
        StateWriteResult changed = await firstValues.CompareExchangeAsync(scope, key, initial.Current!.Version, NodeValue.FromObject(2), timeout.Token);
        Assert.True(changed.Applied);
        StateWriteResult stale = await secondValues.CompareExchangeAsync(scope, key, initial.Current.Version, NodeValue.FromObject(3), timeout.Token);
        Assert.False(stale.Applied);
        Assert.Equal("Persistence.VersionConflict", stale.Reason);
        Assert.Equal(2, (await secondValues.ReadAsync(scope, key, timeout.Token))!.Value.Json.GetInt32());
        Assert.Null(await firstStore.ReadAsync(scope, key, timeout.Token));
        await first.DetachAsync(timeout.Token);
        while (!string.Equals(second.CoordinatorId, secondPeer, StringComparison.Ordinal))
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Null(await secondValues.ReadAsync(scope, key, timeout.Token));
        Assert.True((await secondValues.CompareExchangeAsync(scope, key, null, NodeValue.FromObject(4), timeout.Token)).Applied);
        await first.RejoinAsync(timeout.Token);
        await ReadyAsync(first, second, timeout.Token);
        await second.DetachAsync(timeout.Token);
        while (!string.Equals(first.CoordinatorId, firstPeer, StringComparison.Ordinal))
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Null(await firstValues.ReadAsync(scope, key, timeout.Token));
    }

    /// <summary>登録前の設定用・明示状態用スコープは保持値サービスで更新できません。</summary>
    /// <returns>共有設定を揮発性の保存へ混入させない拒否です。</returns>
    [Fact]
    public async Task ExplicitStateAndConfigurationDoNotUseTransientRetention()
    {
        MemoryRepository repository = new();
        await using PeerCluster cluster = Create(repository, Guid.NewGuid().ToString("N"), "first", LargeSynchronizationTests.Port(), true);
        RuntimeRetainedValues values = new(cluster, new NodeStateStore(repository, "first"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await values.CompareExchangeAsync("configuration", "test", null, NodeValue.FromObject(1)).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentException>(async () => await values.ReadAsync("workflow:test", "user-state").ConfigureAwait(false));
    }

    /// <summary>ローカル担当の保持値は永続操作を作らず、呼出側の配列変更と取消から値を保護します。</summary>
    /// <returns>直接CASの所有権と、記録書込みを伴わない更新です。</returns>
    [Fact]
    public async Task LocalRetentionAvoidsReceiptsAndCopiesReturnedBuffers()
    {
        MemoryRepository repository = new();
        await using PeerCluster cluster = Create(repository, Guid.NewGuid().ToString("N"), "first", LargeSynchronizationTests.Port(), true);
        RuntimeRetainedValues values = new(cluster, new NodeStateStore(repository, "first"));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await cluster.StartAsync(deadline.Token);
        string scope = "workflow:local", key = "runtime:node-output:binary";
        StateWriteResult written = await values.CompareExchangeAsync(scope, key, null, NodeValue.FromBytes(new byte[] { 1 }), deadline.Token);
        Assert.True(written.Applied);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(written.Current!.Value.Binary, out ArraySegment<byte> output));
        output.Array![output.Offset] = 2;
        Assert.Equal(new byte[] { 1 }, (await values.ReadAsync(scope, key, deadline.Token))!.Value.Binary.ToArray());
        StateWriteResult rejected = await values.CompareExchangeAsync(scope, key, null, NodeValue.FromObject(3), deadline.Token);
        Assert.False(rejected.Applied);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(rejected.Current!.Value.Binary, out ArraySegment<byte> conflict));
        conflict.Array![conflict.Offset] = 4;
        Assert.Equal(new byte[] { 1 }, (await values.ReadAsync(scope, key, deadline.Token))!.Value.Binary.ToArray());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await values.CompareExchangeAsync(scope, key, written.Current.Version,
            NodeValue.FromObject(5), cancelled.Token).ConfigureAwait(false));
        Assert.Equal(written.Current.Version, (await values.ReadAsync(scope, key, deadline.Token))!.Version);
        Assert.Empty(await repository.ListAsync("receipts:first", deadline.Token));
        Assert.Null(await repository.ReadAsync(scope, key, deadline.Token));
    }

    /// <summary>実発見と参加状態が成立してから値の共有を開始します。</summary>
    /// <returns>両Peerが同じ通常参加集合を観測した状態です。</returns>
    private static async Task ReadyAsync(PeerCluster first, PeerCluster second, CancellationToken cancellationToken = default)
    {
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>保存障害以外の共有条件を通常Peerと同じ経路で満たします。</summary>
    /// <returns>隔離した実UDP Peerです。</returns>
    private static PeerCluster Create(IDurableRepository repository, string cluster, string peer, int port, bool fresh) => new(
        new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath())
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromSeconds(2) }, repository)
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>新機能追加前の厳密なPeer本文を再現し、未定義の追加項目を受理しません。</summary>
    private sealed record LegacyPeer(string PeerId, Guid InstanceId, string HostId, string Address, int Port, EPeerStatus Status,
        int Priority, DateTimeOffset? ReadySince, DateTimeOffset LastSeen, int? LogPort = null, PeerPersistenceStatus? PersistenceStatus = null);

    /// <summary>通常の共有状態の規則を維持した有限試験用の保存先です。</summary>
    private sealed class MemoryRepository : DurableRepository
    {
        private readonly Lock gate = new();
        private readonly RepositoryDocument document = new();

        /// <summary>一回の保存規則を排他的に適用します。</summary>
        /// <returns>既存の保存操作結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
