using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>共有開始前の確定した未実行と、開始したか不明な中断を区別します。</summary>
public sealed class DetachedSubmissionTests
{
    /// <summary>未実行なら再参加後の反映を妨げず、結果不明なら暗黙に再送しません。</summary>
    /// <returns>固定した障害境界の復旧結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationDistinguishesNotStartedFromInterrupted(bool interrupted)
    {
        string root = Path.Combine(Path.GetTempPath(), "clutch-detached-submit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        DetachedSubmissionRepository repository = new();
        string clusterId = Guid.NewGuid().ToString("N");
        PeerCluster cluster = new(new ClusterOptions(clusterId, "a", "host-a", "127.0.0.1", "127.255.255.255", port, 2, true, root), repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
        DetachedSubmissionRepository secondRepository = new();
        PeerCluster second = new(new ClusterOptions(clusterId, "b", "host-b", "127.0.0.1", "127.255.255.255", port, 1, false, root), secondRepository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
        try
        {
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
            await cluster.StartAsync();
            await second.StartAsync();
            await ManualSafetyFixture.WaitAsync(() => cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
            NodeStateStore states = new(repository, "a");
            using StateReplication replication = new(cluster, repository, states);
            using StateReplication secondReplication = new(second, secondRepository, new(secondRepository, "b"));
            string scope = "workflow:example";
            StateWriteResult initial = await states.CompareExchangeAsync(scope, "key", null, NodeValue.FromObject(1));
            Assert.True(initial.Applied);
            await cluster.DetachAsync();
            StateWriteResult local = await states.CompareExchangeAsync(scope, "key", initial.Current!.Version, NodeValue.FromObject(2));
            Assert.True(local.Applied);
            await cluster.RejoinAsync();
            await ManualSafetyFixture.WaitAsync(() => cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
            if (interrupted)
            {
                repository.AfterSubmission = _ => throw new OperationCanceledException("Interrupted after persisting intent.");
                await Assert.ThrowsAsync<OperationCanceledException>(() => states.ReconcileDetachedAsync());
            }
            else
            {
                repository.AfterSubmission = token => cluster.DetachAsync(token);
                await states.ReconcileDetachedAsync();
                Assert.True(cluster.IsDetached);
                await cluster.RejoinAsync();
                await ManualSafetyFixture.WaitAsync(() => cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
            }
            await states.ReconcileDetachedAsync();
            await states.ReconcileDetachedAsync();
            int expected = 2;
            if (interrupted)
            {
                expected = 1;
            }
            Assert.Equal(expected, (await repository.ReadAsync(scope, "key"))!.Value!.Json.GetInt32());
            DurableEntry branch = Assert.Single(await repository.ListAsync("peer:a:detached-state"));
            Assert.Equal(interrupted, branch.Value!.Json.Deserialize<DetachedStateEntry>()!.Pending);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
