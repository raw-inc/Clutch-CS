using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>相手の未確定準備を、過去本文の再書出し前に確認することを検証します。</summary>
public sealed class PreparedSynchronizationCostTests
{
    /// <summary>受信側が保留中は書出しを始めず、準備が終了すれば通常同期へ戻ります。</summary>
    /// <returns>実UDPの能力照会による負荷抑制と、その後の自動同期です。</returns>
    [Fact]
    public async Task PendingReceiverDoesNotCauseRepeatedSnapshotExportsAsync()
    {
        using MediaTestFiles files = new();
        CountingRepository sender = new();
        SqliteRepository receiver = new(Path.Combine(files.Root, "receiver.db"));
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        PeerCluster first = Create(sender, files.Root, cluster, "sender", port, true);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        PeerCluster second = Create(receiver, files.Root, cluster, "receiver", port, false);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        await Task.Delay(200, timeout.Token);
        Guid held = Guid.NewGuid();
        await receiver.PrepareAsync(held, [new("workflow:held", "value", null, NodeValue.FromObject(1))], timeout.Token);
        sender.Reset();
        await sender.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:changed", "value", null, NodeValue.FromObject(42))], timeout.Token);
        await Task.Delay(500, timeout.Token);
        Assert.Equal(0, sender.Exports);
        Assert.Null(await receiver.ReadAsync("workflow:changed", "value", timeout.Token));
        await receiver.AbortPreparedAsync(held, timeout.Token);
        string scope = "workflow:changed", key = "value";
        while ((await receiver.ReadAsync(scope, key, timeout.Token))?.Value?.Json.GetInt32() != 42)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.True(0 < sender.Exports);
    }

    /// <summary>実通信だけを行う隔離クラスタを構成します。</summary>
    /// <returns>短い通知周期の試験Peerです。</returns>
    private static PeerCluster Create(IDurableRepository store, string root, string cluster, string peer, int port, bool fresh)
        => new(new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(40), PeerTimeout = TimeSpan.FromMilliseconds(500), RequestTimeout = TimeSpan.FromMilliseconds(300) }, store)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>重い書出し境界だけを数え、元の保存と通信規則は変更しません。</summary>
    private sealed class CountingRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();
        private int exports;

        internal int Exports => Volatile.Read(ref exports);

        /// <summary>初回参加の費用を計測から除きます。</summary>
        internal void Reset() => Interlocked.Exchange(ref exports, 0);

        /// <summary>通常書出しの開始回数です。</summary>
        /// <returns>実際の保存スナップショットです。</returns>
        public override ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref exports);
            return base.ExportAsync(cancellationToken);
        }

        /// <summary>現在状態の書出しも重い同期の開始として扱います。</summary>
        /// <returns>実際の現在状態です。</returns>
        public override ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref exports);
            return base.ReadCurrentStateAsync(cancellationToken);
        }

        /// <summary>同じ保存規則を排他的に実行します。</summary>
        /// <returns>一取引の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
