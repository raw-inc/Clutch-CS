using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>全体4MiBを超える共有保存でも、新規Peerが完全同期できることを検証します。</summary>
public sealed class LargeSynchronizationTests
{
    /// <summary>一メッセージ以内の通常変更を累積し、currentと操作記録を全て取り込みます。</summary>
    /// <returns>実SQLiteと実UDPによる同期確認です。</returns>
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    public async Task AccumulatedSnapshotSynchronizesWithoutRaisingDatagramLimit(int count)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-large-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = Port();
            string clusterId = Guid.NewGuid().ToString("N");
            SqliteRepository source = new(Path.Combine(root, "a.db"));
            SqliteRepository destination = new(Path.Combine(root, "b.db"));
            await using PeerCluster first = Create(source, root, clusterId, "a", port, true);
            await first.StartAsync().ConfigureAwait(true);
            List<Guid> operations = [];
            for (int index = 0; index < count; index++)
            {
                Guid operation = Guid.NewGuid();
                operations.Add(operation);
                DurableReceipt saved = await first.ReplicateAsync(operation, [new DurableMutation("workflow:large", "value" + index, null,
                    NodeValue.FromObject(new string('x', 150000)))]).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, saved.Outcome);
            }
            RepositorySnapshot original = await source.ExportAsync().ConfigureAwait(true);
            if (count == 16)
            {
                Assert.True(4 * 1024 * 1024 < JsonSerializer.SerializeToUtf8Bytes(original).Length);
            }
            await using PeerCluster second = Create(destination, root, clusterId, "b", port, false);
            second.ReadinessCheck = async token => (await destination.ReadAsync("workflow:large", "value0", token).ConfigureAwait(false))?.Value is not null;
            await second.StartAsync().ConfigureAwait(true);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
            while (second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status != EPeerStatus.Ready)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(true);
            }
            Assert.Equal(count, (await destination.ListAsync("workflow:large", timeout.Token).ConfigureAwait(true)).Count);
            foreach (Guid id in operations)
            {
                Assert.Equal(EOutcome.Succeeded, (await destination.ReadReceiptAsync(id, timeout.Token).ConfigureAwait(true))!.Outcome);
            }
            Assert.Null(second.SynchronizationProblem);
            Assert.Equal(await source.GetSharedVersionAsync(timeout.Token).ConfigureAwait(true), await destination.GetSharedVersionAsync(timeout.Token).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>自分の試験だけが使うUDP ポートを選びます。</summary>
    /// <returns>使用可能なポートです。</returns>
    internal static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>明示したローカルNICで試験Peerを構成します。</summary>
    /// <returns>起動前のPeerです。</returns>
    internal static PeerCluster Create(SqliteRepository repository, string root, string id, string peer, int port, bool fresh, bool preferRequestStream = true) => new(
        new ClusterOptions(id, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            PreferRequestStream = preferRequestStream,
        }, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
}
