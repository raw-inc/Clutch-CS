using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.ClientHost.Files;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>実UDPの照会回数で、待機時の間引きと進捗・欠測時の更新を区別します。</summary>
public sealed class SharedTransferPollingTests
{
    /// <summary>無操作の終了キューだけを再利用し、表示中・進行中・結果不明・失敗は再照会します。</summary>
    /// <returns>キャッシュによって確認不能や再起動を成功へ取り違えないことです。</returns>
    [Fact]
    public async Task IdleQueuesBackOffButActiveViewsAndUnconfirmedReadsStayResponsive()
    {
        string directory = Directory.CreateTempSubdirectory("contents-transfer-poll-").FullName;
        try
        {
            string clusterId = Guid.NewGuid().ToString("N");
            int port;
            using (UdpClient available = new(0))
            {
                port = ((IPEndPoint)available.Client.LocalEndPoint!).Port;
            }
            string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
            PeerCluster peer = new(new(clusterId, "p", "host", "127.0.0.1", "127.255.255.255", port, 1, true, directory), new SqliteRepository(Path.Combine(directory, "state.db")))
            {
                ReadinessCheck = _ => ValueTask.FromResult(true),
            };
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
            UdpObserver observer = new(new("observer", new(nic, "127.0.0.1", "127.255.255.255", port)));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
            ObserverConnection connection = new(Path.Combine(directory, "client"));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
            EditorTestClock clock = new();
            FileTransferPeer target = new(clusterId, "p", peer.LocalInstanceId, port);
            FileTransferRequest request = new(Guid.NewGuid(), target, target, "unused", "unused");
            string state = "Succeeded";
            bool fail = false;
            int reads = 0;
            FileTransferJob[]? entries = null;
            peer.RegisterInspection(ManagementWire.ReadKind, (_, _) =>
            {
                Interlocked.Increment(ref reads);
                if (fail)
                {
                    return Task.FromResult(new ClusterResponse(EOutcome.Failed));
                }
                FileTransferJob[] jobs = entries ?? [new(request, state, 0, 0, clock.GetUtcNow())];
                return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new ManagementReply(EOutcome.Succeeded, NodeValue.FromObject(jobs)))));
            });
            await peer.StartAsync();
            await observer.StartAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (!observer.GetClusters().SelectMany(cluster => cluster.Peers).Any(item => item.InstanceId == peer.LocalInstanceId))
            {
                await Task.Delay(25, timeout.Token);
            }
            using ClientCommandLedger ledger = new(directory);
            using SharedTransfers transfers = new(connection, new((_, _) => observer), ledger, clock);
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(1, reads);
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(1, reads);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(2, reads);
            _ = transfers.Read();
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(3, reads);
            clock.Advance(TimeSpan.FromSeconds(10));
            state = "Copying";
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(5, reads);
            state = "Unknown";
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(7, reads);
            fail = true;
            Assert.False(await transfers.RefreshPeerAsync(target));
            Assert.False(await transfers.RefreshPeerAsync(target));
            Assert.Equal(9, reads);
            fail = false;
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(10, reads);
            Assert.False(await transfers.RefreshPeerAsync(target with { InstanceId = Guid.NewGuid() }));
            FileTransferJob queued = new(request with { Id = Guid.NewGuid() }, "Queued", 0, 100, clock.GetUtcNow().AddDays(-1));
            entries = Enumerable.Range(0, 130).Select(index => new FileTransferJob(request with { Id = Guid.NewGuid() }, "Succeeded", 100, 100, clock.GetUtcNow().AddSeconds(index)))
                .Append(queued).ToArray();
            _ = transfers.Read(progress: true);
            Assert.True(await transfers.RefreshPeerAsync(target));
            SharedTransferQueueState progress = transfers.Read(progress: true);
            Assert.Equal(131, progress.TotalJobs);
            Assert.Equal(8, progress.Jobs.Count);
            Assert.Equal(queued.Request.Id, progress.Jobs[0].Request.Id);
            Assert.DoesNotContain(transfers.Read().Jobs, job => job.Request.Id == queued.Request.Id);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
