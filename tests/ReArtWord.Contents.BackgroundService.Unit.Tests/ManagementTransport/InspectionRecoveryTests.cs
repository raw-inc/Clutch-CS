using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>管理の参照だけを再照会し、確定失敗・書込み・別実体へ展開しないことを実UDPで検証します。</summary>
public sealed class InspectionRecoveryTests
{
    /// <summary>一往復の欠測後に回復し、連続欠測にも回数上限を適用します。</summary>
    /// <returns>実配送と元要求の同一性の確認です。</returns>
    [Theory]
    [InlineData(false, false, true, 2)]
    [InlineData(true, false, true, 3)]
    [InlineData(false, true, true, 1)]
    [InlineData(true, false, false, 1)]
    public async Task OnlyMissingReadRepliesAreRetried(bool dropEveryReply, bool reject, bool readOnly, int expectedDeliveries)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal))).InterfaceId;
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        UdpObserver observer = new(new("test", binding) { RequestTimeout = TimeSpan.FromMilliseconds(500) });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = ((IAsyncDisposable)observer).ConfigureAwait(true);
        target.Initialize(nic);
        ConcurrentDictionary<Guid, ClusterRequest> deliveries = new();
        Guid first = Guid.Empty;
        Lock gate = new();
        target.Received = async (message, _, token) =>
        {
            string inspect = "inspect", execute = "request";
            if (!string.Equals(message.Kind, inspect, StringComparison.Ordinal) && !string.Equals(message.Kind, execute, StringComparison.Ordinal))
            {
                return;
            }
            ClusterRequest request = message.Body.Deserialize<ClusterRequest>(ContractJson.Options)!;
            deliveries.TryAdd(message.Id, request);
            bool drop;
            using (gate.EnterScope())
            {
                if (first == Guid.Empty)
                {
                    first = message.Id;
                }
                drop = !reject && (dropEveryReply || message.Id == first);
            }
            if (drop)
            {
                return;
            }
            EOutcome outcome = EOutcome.Succeeded;
            if (reject)
            {
                outcome = EOutcome.NotExecuted;
            }
            ClusterResponse response = new(outcome, NodeValue.FromObject(new ManagementReply(outcome, NodeValue.FromObject(true))));
            await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
        };
        target.Start();
        await observer.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        long sequence = 0;
        while (observer.GetClusters().Count == 0)
        {
            PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null,
                JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), timeout.Token);
            await Task.Delay(20, timeout.Token);
        }
        ManagementCommand command = new(Guid.NewGuid(), cluster, "target", instance, DateTimeOffset.UtcNow.AddSeconds(8), "test", null);
        ManagementReply reply;
        if (readOnly)
        {
            reply = await observer.InspectWithRetryAsync(command, timeout.Token);
        }
        else
        {
            reply = await observer.ExecuteAsync(command, timeout.Token);
        }
        Assert.Equal(expectedDeliveries, deliveries.Count);
        Assert.All(deliveries.Values, request =>
        {
            Assert.Equal(command.RequestId, request.Id);
            Assert.Equal(instance, request.TargetInstanceId);
            Assert.Equal(command.ExpiresAt, request.ExpiresAt);
        });
        EOutcome expected = EOutcome.Succeeded;
        if (dropEveryReply)
        {
            expected = EOutcome.Unknown;
        }
        if (reject)
        {
            expected = EOutcome.NotExecuted;
        }
        Assert.Equal(expected, reply.Outcome);
    }
}
