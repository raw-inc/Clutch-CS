using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>取込み結果の通知前に解消された分岐を、遅れた通知で再び隔離しません。</summary>
public sealed class SynchronizationConflictRaceTests
{
    /// <summary>保存後の競合解消と、保存時点の未統合結果の到着を明示的に交差させます。</summary>
    /// <returns>現在の候補と参加状態が一致することです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolutionBeforeImportReturnsDoesNotRestoreConflict(bool resolve)
    {
        PausedImport repository = new();
        string scope = "workflow:conflict-race", key = "value";
        string id = Guid.NewGuid().ToString("N");
        ClusterOptions options = new(id, "target", "target", "127.0.0.1", "127.255.255.255", LargeSynchronizationTests.Port(), 1, true, Path.GetTempPath())
        {
            HeartbeatInterval = TimeSpan.FromHours(1),
        };
        PeerCluster cluster = new(options, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        Assert.Equal(EPeerStatus.Ready, Assert.Single(cluster.GetPeers()).Status);
        DurableReceipt initial = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null, NodeValue.FromObject("base"))]).ConfigureAwait(true);
        DurableReceipt local = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, initial.Entries[0].Version, NodeValue.FromObject("local"))]).ConfigureAwait(true);
        RepositorySnapshot incoming = new([new(scope, key, Guid.NewGuid().ToString("N"), NodeValue.FromObject("remote"), initial.Entries[0].Version)], []);
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        SyncExportStore files = new(cluster, repository, options, TimeProvider.System, () => new(network, "127.0.0.1"));
        await using ConfiguredAsyncDisposable filesLifetime = files.ConfigureAwait(true);
        SyncDownload download = new(cluster, repository, files, options, TimeProvider.System, () => new(network, "127.0.0.1"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<bool> importing = download.ImportSnapshotAsync(incoming, timeout.Token);
        try
        {
            await repository.Imported.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            if (resolve)
            {
                string[] candidates = (await repository.GetConflictsAsync(scope, timeout.Token).ConfigureAwait(true)).Select(entry => entry.Version).ToArray();
                DurableReceipt resolved = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null, local.Entries[0].Value, candidates)], timeout.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, resolved.Outcome);
            }
        }
        finally
        {
            repository.Release.TrySetResult();
            await ((Task)importing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        Assert.Equal(resolve, await importing.ConfigureAwait(true));
        Assert.Equal(resolve ? EPeerStatus.Ready
                             : EPeerStatus.Conflict, Assert.Single(cluster.GetPeers()).Status);
        Assert.Equal(resolve ? 0
                             : 2, (await repository.GetConflictsAsync(scope, timeout.Token).ConfigureAwait(true)).Count);
    }

    /// <summary>保存を完了してから結果だけを保留し、別操作の解消を許可します。</summary>
    private sealed class PausedImport : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        internal TaskCompletionSource Imported
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>元のマージ規則を実行し、確定済みの結果だけを待機させます。</summary>
        /// <returns>保存時点の競合判定です。</returns>
        public override async ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            bool merged = await base.ImportAsync(snapshot, cancellationToken).ConfigureAwait(false);
            Imported.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return merged;
        }

        /// <summary>各保存操作を直列化し、通知の待機中は排他を保持しません。</summary>
        /// <returns>原子的に確認した操作結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
