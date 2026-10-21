using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新書込みの応答消失と、操作記録照会自体の一時欠測を区別します。</summary>
public sealed class UpdateObserverTests
{
    /// <summary>照会パケットの消失後も元結果だけを読み、既知の未完了記録は再実行しません。</summary>
    /// <returns>実UDPで一度の副作用と照会だけの回復を確認します。</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    public async Task ReceiptLossRetriesOnlyTheOriginalResultQueryAsync(bool loseFirstQuery, bool loseEveryQuery, bool firstReceiptMissing = false)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal))).InterfaceId;
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        target.Initialize(nic);
        UpdateObserver observer = new(binding, cluster);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
        ConcurrentDictionary<Guid, byte> effects = new();
        ConcurrentDictionary<Guid, byte> deliveries = new();
        ConcurrentDictionary<Guid, byte> queries = new();
        Lock gate = new();
        Guid firstQuery = Guid.Empty;
        target.Received = async (message, _, token) =>
        {
            string execute = "request";
            if (string.Equals(message.Kind, execute, StringComparison.Ordinal))
            {
                effects.TryAdd(message.Body.Deserialize<ClusterRequest>()!.Id, 0);
                deliveries.TryAdd(message.Id, 0);
                return;
            }
            string queryKind = "receipt.query";
            if (!string.Equals(message.Kind, queryKind, StringComparison.Ordinal))
            {
                return;
            }
            bool drop;
            using (gate.EnterScope())
            {
                if (firstQuery == Guid.Empty)
                {
                    firstQuery = message.Id;
                }
                drop = loseFirstQuery && (loseEveryQuery || firstQuery == message.Id);
            }
            queries.TryAdd(message.Id, 0);
            if (!drop)
            {
                ClusterRequestStatus result = new(false, Reason: "Execution.StartedWithoutResult");
                if (loseFirstQuery || firstReceiptMissing)
                {
                    result = new(false, new(EOutcome.Succeeded, NodeValue.FromObject(new ManagementReply(EOutcome.Succeeded, NodeValue.FromObject(true)))));
                }
                if (firstReceiptMissing && firstQuery == message.Id)
                {
                    result = new(false, Reason: "Execution.ReceiptMissing");
                }
                await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "reply", message.SenderId, message.Id, JsonSerializer.SerializeToElement(result), message.InstanceId), token).ConfigureAwait(false);
            }
        };
        target.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task<PeerInfo> found = observer.FindAsync("target", instance, timeout.Token);
        long sequence = 0;
        while (!found.IsCompleted)
        {
            PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), timeout.Token).ConfigureAwait(true);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        await found.ConfigureAwait(true);
        ManagementCommand command = new(Guid.NewGuid(), cluster, "target", instance, DateTimeOffset.UtcNow.AddSeconds(6), "write", NodeValue.FromObject(true));
        ManagementReply reply = await observer.SendAsync(command, false, timeout.Token).ConfigureAwait(true);
        Assert.Equal(command.RequestId, Assert.Single(effects).Key);
        Assert.Single(deliveries);
        if (loseFirstQuery || firstReceiptMissing)
        {
            EOutcome expected = EOutcome.Succeeded;
            if (loseEveryQuery)
            {
                expected = EOutcome.Unknown;
            }
            Assert.Equal(expected, reply.Outcome);
            Assert.True(2 <= queries.Count);
        }
        else
        {
            Assert.Equal(EOutcome.Unknown, reply.Outcome);
            Assert.Single(queries);
        }
    }
}
