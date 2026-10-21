using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>転送を分割しても両側の分岐候補を失わず、勝手にReadyへ戻しません。</summary>
public sealed class SynchronizationConflictTests
{
    /// <summary>同じ親版から分岐した二Peerは両側で候補を比較できます。</summary>
    /// <returns>実再参加と既存Import競合の確認です。</returns>
    [Fact]
    public async Task ConflictingBranchesRemainAvailableOnBothPeers()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-sync-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = LargeSynchronizationTests.Port();
            string clusterId = Guid.NewGuid().ToString("N");
            SqliteRepository a = new(Path.Combine(root, "a.db"));
            SqliteRepository b = new(Path.Combine(root, "b.db"));
            await using PeerCluster first = Peer(a, root, clusterId, "a", port, true);
            await first.StartAsync().ConfigureAwait(true);
            _ = await first.ReplicateAsync(Guid.NewGuid(), [new DurableMutation("workflow:conflict", "key", null, NodeValue.FromObject("base"))]).ConfigureAwait(true);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await using (PeerCluster initial = Peer(b, root, clusterId, "b", port, false))
            {
                await initial.StartAsync(timeout.Token).ConfigureAwait(true);
                while (initial.GetPeers().Single(peer => peer.InstanceId == initial.LocalInstanceId).Status != EPeerStatus.Ready)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                }
            }
            string returningPeer = "b";
            while (first.GetPeers().Any(peer => string.Equals(peer.PeerId, returningPeer, StringComparison.Ordinal) && peer.Status != EPeerStatus.Unavailable))
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
            DurableEntry original = (await a.ReadAsync("workflow:conflict", "key", timeout.Token).ConfigureAwait(true))!;
            _ = await a.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(original.Scope, original.Key, original.Version, NodeValue.FromObject("left"))], timeout.Token).ConfigureAwait(true);
            _ = await b.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(original.Scope, original.Key, original.Version, NodeValue.FromObject("right"))], timeout.Token).ConfigureAwait(true);
            await using PeerCluster returning = Peer(b, root, clusterId, "b", port, false);
            await returning.StartAsync(timeout.Token).ConfigureAwait(true);

            // 候補の永続コミット後に参加状態を更新するため、保存だけで同期完了と判断しません。
            try
            {
                while ((await a.GetConflictsAsync(original.Scope, timeout.Token).ConfigureAwait(true)).Count == 0
                    || (await b.GetConflictsAsync(original.Scope, timeout.Token).ConfigureAwait(true)).Count == 0
                    || first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).Status != EPeerStatus.Conflict
                    || returning.GetPeers().Single(peer => peer.InstanceId == returning.LocalInstanceId).Status != EPeerStatus.Conflict)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail(System.Text.Json.JsonSerializer.Serialize(new
                {
                    First = first.GetPeers(), FirstProblem = first.SynchronizationProblem,
                    Returning = returning.GetPeers(), ReturningProblem = returning.SynchronizationProblem,
                    FirstConflicts = (await a.GetConflictsAsync(original.Scope, CancellationToken.None).ConfigureAwait(true)).Count,
                    ReturningConflicts = (await b.GetConflictsAsync(original.Scope, CancellationToken.None).ConfigureAwait(true)).Count,
                }));
            }
            Assert.Equal(EPeerStatus.Conflict, first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).Status);
            Assert.Equal(EPeerStatus.Conflict, returning.GetPeers().Single(peer => peer.InstanceId == returning.LocalInstanceId).Status);
            Assert.Equal("left", (await a.ReadAsync(original.Scope, original.Key, timeout.Token).ConfigureAwait(true))!.Value!.GetString());
            Assert.Equal("right", (await b.ReadAsync(original.Scope, original.Key, timeout.Token).ConfigureAwait(true))!.Value!.GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>再起動前実体の発見猶予だけを試験用に短縮します。</summary>
    /// <returns>保存先を継続するPeerです。</returns>
    private static PeerCluster Peer(SqliteRepository repository, string root, string id, string peer, int port, bool fresh) => new(
        new ClusterOptions(id, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromMilliseconds(500), PeerTimeout = TimeSpan.FromMilliseconds(500),
        }, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
}
