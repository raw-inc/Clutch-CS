using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
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
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>実UDPで、待機時の照会間隔と進捗・欠測時の更新を検証します。</summary>
public sealed class SharedTransferPollingTests
{
    /// <summary>無操作の終了キューだけを再利用し、表示中・進行中・結果不明・失敗は再照会します。</summary>
    /// <param name="dropFirstReply">キャッシュの期限切れ後、最初の応答を破棄するかどうか。</param>
    /// <returns>キャッシュによって確認不能や再起動を成功へ取り違えないことです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleQueuesBackOffButActiveViewsAndUnconfirmedReadsStayResponsive(bool dropFirstReply)
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
            Guid instance = Guid.NewGuid();
            UdpTransport peer = new(new("p", "127.0.0.1", "127.255.255.255", port, clusterId), instance, TimeProvider.System);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = ((IAsyncDisposable)peer).ConfigureAwait(true);
            peer.Initialize(nic);
            UdpObserver observer = new(new("observer", new(nic, "127.0.0.1", "127.255.255.255", port)));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
            ObserverConnection connection = new(Path.Combine(directory, "client"));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
            EditorTestClock clock = new();
            FileTransferPeer target = new(clusterId, "p", instance, port);
            FileTransferRequest request = new(Guid.NewGuid(), target, target, "unused", "unused");
            string state = "Succeeded";
            bool fail = false;

            // 同じIDのUDP再送は、新しい照会として数えません。
            ConcurrentDictionary<Guid, int> deliveries = new();
            FileTransferJob[]? entries = null;
            peer.Received = async (message, _, token) =>
            {
                string inspectionKind = "inspect";
                if (!string.Equals(message.Kind, inspectionKind, StringComparison.Ordinal))
                {
                    return;
                }
                ClusterRequest inspection = message.Body.Deserialize<ClusterRequest>(ContractJson.Options)!;
                int received = deliveries.AddOrUpdate(inspection.Id, 1, (_, count) => count + 1);
                if (dropFirstReply && deliveries.Count == 2 && received == 1)
                {
                    return;
                }
                FileTransferJob[] jobs = entries ?? [new(request, state, 0, 0, clock.GetUtcNow())];
                ClusterResponse response = new(EOutcome.Succeeded, NodeValue.FromObject(new ManagementReply(EOutcome.Succeeded, NodeValue.FromObject(jobs))));
                if (fail)
                {
                    response = new(EOutcome.Failed);
                }
                await peer.SendAsync(WireMessage.Reply(message, "p", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
            };
            peer.Start();
            await observer.StartAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            long sequence = 0;
            while (!observer.GetClusters().SelectMany(cluster => cluster.Peers).Any(item => item.InstanceId == instance))
            {
                PeerInfo announced = new("p", instance, "host", "127.0.0.1", port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                await peer.SendAsync(new(Guid.NewGuid(), clusterId, "p", instance, "hello", null, null,
                    JsonSerializer.SerializeToElement(new PeerAnnouncement(announced, "digest", "p", null, ++sequence))), timeout.Token);
                await Task.Delay(25, timeout.Token);
            }
            using ClientCommandLedger ledger = new(directory);
            using SharedTransfers transfers = new(connection, new((_, _) => observer), ledger, clock);
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Single(deliveries);
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Single(deliveries);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(2, deliveries.Count);
            if (dropFirstReply)
            {
                Assert.Contains(deliveries.Values, count => 1 < count);
            }
            _ = transfers.Read();
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(3, deliveries.Count);
            clock.Advance(TimeSpan.FromSeconds(10));
            state = "Copying";
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(5, deliveries.Count);
            state = "Unknown";
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(7, deliveries.Count);
            fail = true;
            Assert.False(await transfers.RefreshPeerAsync(target));
            Assert.False(await transfers.RefreshPeerAsync(target));
            Assert.Equal(9, deliveries.Count);
            fail = false;
            Assert.True(await transfers.RefreshPeerAsync(target));
            Assert.Equal(10, deliveries.Count);
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
