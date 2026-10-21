using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>作業容量超過でも既存Peerと同期枠を壊しません。</summary>
public sealed class SynchronizationCapacityTests
{
    /// <summary>大きすぎる書出しを明示拒否し、再確認で予約枠を漏らしません。</summary>
    /// <returns>容量制限と部分未適用の確認です。</returns>
    [Fact]
    public async Task CapacityFailureKeepsReceiverSyncingAndReleasesExportSlot()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-sync-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = LargeSynchronizationTests.Port();
            string id = Guid.NewGuid().ToString("N");
            SqliteRepository source = new(Path.Combine(root, "a.db"));
            SqliteRepository destination = new(Path.Combine(root, "b.db"));
            ClusterOptions options = new(id, "a", "a", "127.0.0.1", "127.255.255.255", port, 1, true, root)
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromMilliseconds(500), MaximumSyncSnapshotBytes = 65536, MaximumSyncSessions = 1,
            };
            PeerCluster first = new(options, source) { ReadinessCheck = _ => ValueTask.FromResult(true) };
            await using ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
            await first.StartAsync().ConfigureAwait(true);
            _ = await first.ReplicateAsync(Guid.NewGuid(), [new DurableMutation("workflow:capacity", "value", null, NodeValue.FromObject(new string('x', 90000)))]).ConfigureAwait(true);
            await using PeerCluster second = LargeSynchronizationTests.Create(destination, root, id, "b", port, false);
            await second.StartAsync().ConfigureAwait(true);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            string reason = "Sync.SnapshotCapacity";
            while (!string.Equals(second.SynchronizationProblem, reason, StringComparison.Ordinal))
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(true);
            }
            await Task.Delay(1000, timeout.Token).ConfigureAwait(true);
            Assert.Equal(reason, second.SynchronizationProblem);
            Assert.Equal(EPeerStatus.Syncing, second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status);
            Assert.Equal(EPeerStatus.Ready, first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).Status);
            Assert.Empty(await destination.ListAsync("workflow:capacity", timeout.Token).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
