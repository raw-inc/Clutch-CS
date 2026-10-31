using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>並行転送が完全本文の同時展開数を増やさず、取消時も枠を回収することを確認します。</summary>
public sealed class SnapshotMaterializationTests
{
    /// <summary>書出しの所有数を保ったまま、大きな本文の復元だけを直列化します。</summary>
    /// <returns>同時復元数、待機取消後の作成、同じ本文の固定書出しです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelExportsDoNotMaterializeMultipleSnapshotsAsync(bool cancelQueued)
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        using PausedRepository repository = new();
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".");
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        SyncExportStore exports = new(peer.Target, repository, options, TimeProvider.System, () => new(network, local.Address));
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource queued = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<SyncManifest> first = exports.CreateAsync(local, true, timeout.Token);
        Task<SyncManifest>? second = null;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            second = exports.CreateAsync(local, true, queued.Token);
            Assert.Equal(1, repository.Calls);
            if (cancelQueued)
            {
                await queued.CancelAsync().ConfigureAwait(true);
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second.ConfigureAwait(false)).ConfigureAwait(true);
                second = exports.CreateAsync(local, true, timeout.Token);
            }
            repository.Permits.Release(2);
            SyncManifest[] results = await Task.WhenAll(first, second).ConfigureAwait(true);
            Assert.Equal(1, repository.Peak);
            Assert.Equal(2, repository.Calls);
            Assert.NotEqual(results[0].Id, results[1].Id);
            Assert.Equal(results[0].Hash, results[1].Hash);
            foreach (SyncManifest result in results)
            {
                await exports.ReleaseAsync(result.Id, timeout.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            await Task.WhenAll(first, second ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>許可した回数だけ、同じ大きさの本文を新しく取得します。</summary>
    private sealed class PausedRepository : DurableRepository, IDisposable
    {
        private int active;

        internal int Calls
        {
            get; private set;
        }

        internal int Peak
        {
            get; private set;
        }

        internal SemaphoreSlim Permits
        {
            get;
        } = new(0);

        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>未解放の同時取得を数え、試験が許可してから本文を渡します。</summary>
        /// <returns>新しく復元した固定内容です。</returns>
        public override async ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Peak = Math.Max(Peak, Interlocked.Increment(ref active));
            Entered.TrySetResult();
            try
            {
                await Permits.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new([new("workflow:budget", "payload", "version", NodeValue.FromObject(new string('x', 1048576)))], []);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        /// <summary>書出し以外の暗黙操作を禁止します。</summary>
        /// <returns>想定外操作の例外です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write) => throw new InvalidOperationException("Unexpected persistence operation.");

        /// <summary>試験が所有する待機資源を解放します。</summary>
        public void Dispose() => Permits.Dispose();
    }
}
