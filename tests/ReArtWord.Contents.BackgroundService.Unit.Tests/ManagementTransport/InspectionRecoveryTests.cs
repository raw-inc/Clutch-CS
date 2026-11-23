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
            if (string.Equals(request.Kind, RequestClockSnapshot.Capability, StringComparison.Ordinal))
            {
                // 時計照会には旧Coreと同じ未対応の応答を返します。このテストでは、その後の操作や結果照会の応答だけを失わせます。
                ClusterResponse unsupported = new(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable");
                await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(unsupported)), token).ConfigureAwait(false);
                return;
            }
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

    /// <summary>照会元の日時が進んでも、最初の欠測後は元の受付期間が残っている間だけ再照会します。</summary>
    /// <returns>実UDPで送った照会の回数と結果の確認です。</returns>
    [Theory]
    [InlineData(false, EOutcome.Succeeded, 2)]
    [InlineData(true, EOutcome.Unknown, 1)]
    public async Task InspectionRetryUsesTheOriginalElapsedDeadlineAfterUtcAdjustment(bool elapsedDeadlinePassed, EOutcome expectedOutcome, int expectedDeliveries)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal))).InterfaceId;
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        AdmissionTestClock clock = new();
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        UdpObserver observer = new(new("test", binding) { RequestTimeout = TimeSpan.FromSeconds(1) }, clock);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = ((IAsyncDisposable)observer).ConfigureAwait(true);
        target.Initialize(nic);
        TimeSpan period = TimeSpan.FromMinutes(2);
        ConcurrentDictionary<Guid, ClusterRequest> deliveries = new();
        Guid first = Guid.Empty;
        Lock gate = new();
        target.Received = async (message, _, token) =>
        {
            string inspect = "inspect";
            if (!string.Equals(message.Kind, inspect, StringComparison.Ordinal))
            {
                return;
            }
            deliveries.TryAdd(message.Id, message.Body.Deserialize<ClusterRequest>(ContractJson.Options)!);
            using (gate.EnterScope())
            {
                if (first == Guid.Empty)
                {
                    first = message.Id;
                    clock.Adjust(TimeSpan.FromMinutes(3));
                    if (elapsedDeadlinePassed)
                    {
                        clock.Advance(period);
                    }
                }
                if (message.Id == first)
                {
                    return;
                }
            }
            ClusterResponse response = new(EOutcome.Succeeded, NodeValue.FromObject(new ManagementReply(EOutcome.Succeeded, NodeValue.FromObject(true))));
            await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
        };
        target.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await observer.StartAsync(timeout.Token).ConfigureAwait(true);
        long sequence = 0;
        while (observer.GetClusters().Count == 0)
        {
            PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, clock.GetUtcNow(), clock.GetUtcNow());
            await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null,
                JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), timeout.Token).ConfigureAwait(true);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }

        ManagementCommand command = ManagementCommand.Start(Guid.NewGuid(), cluster, "target", instance, period, "setup.client.info", null, clock);
        ManagementReply reply = await observer.InspectWithRetryAsync(command, timeout.Token).ConfigureAwait(true);

        Assert.Equal(expectedOutcome, reply.Outcome);
        Assert.Equal(expectedDeliveries, deliveries.Count);
        Assert.All(deliveries.Values, request =>
        {
            Assert.Equal(command.RequestId, request.Id);
            Assert.Equal(instance, request.TargetInstanceId);
            Assert.Equal(command.ExpiresAt, request.ExpiresAt);
        });
    }
}
