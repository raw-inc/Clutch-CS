using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>実Coreを別プロセスで起動してUDP管理境界を確認します。</summary>
public sealed class ObserverProcessTests
{
    /// <summary>2Coreの発見・非参加・共有保存と別Peerからの読取を実通信で確認します。</summary>
    /// <returns>実プロセスの結合検証完了。</returns>
    [Fact]
    public async Task TwoCoreProcessesCanBeManagedWithoutJoiningOrUsingHttp()
    {
        string clusterId = Guid.NewGuid().ToString("N");
        int port;
        using (UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }
        string address = "127.0.0.1";
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(candidate => string.Equals(candidate.Address, address, StringComparison.Ordinal)));
        UdpObserver observer = new(new ObserverOptions("process-test", new ClientNetworkBinding(network.InterfaceId, address, "127.255.255.255", port)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(false);
        await observer.StartAsync();
        ObserverCoreProcess first = new(clusterId, "a", port, network.InterfaceId, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        ObserverCoreProcess second = new(clusterId, "b", port, network.InterfaceId, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        DiscoveredCluster? discovered = null;
        while (discovered is null || discovered.Peers.Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            first.EnsureRunning();
            second.EnsureRunning();
            await Task.Delay(50, timeout.Token);
            discovered = observer.GetClusters().SingleOrDefault();
        }
        PeerInfo a = discovered.Peers.Single(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal));
        PeerInfo b = discovered.Peers.Single(peer => string.Equals(peer.PeerId, "b", StringComparison.Ordinal));
        ManagementReply status = await observer.InspectAsync(Command(clusterId, a, "status", NodeValue.Null), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, status.Outcome);
        Assert.Equal(2, status.Value!.Json.GetProperty("peers").GetArrayLength());
        WorkflowSaveRequest save = new(Guid.NewGuid(), new WorkflowDefinition("observer-process", "UDP saved", [], []), null);
        ManagementCommand command = Command(clusterId, a, "workflows.save", NodeValue.FromObject(save));
        ManagementReply result = await observer.ExecuteAsync(command, timeout.Token);

        // 配送待ちの上限を超えても、同じ保存要求の結果だけを照会します。
        while (result.Outcome == EOutcome.Unknown)
        {
            ManagementQueryResult confirmation = await observer.QueryAsync(command, timeout.Token).ConfigureAwait(true);
            if (confirmation.Reply is not null)
            {
                result = confirmation.Reply;
                break;
            }
            await Task.Delay(50, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("succeeded", result.Value!.Json.GetProperty("shared").GetProperty("outcome").GetString());
        Assert.Equal(EOutcome.Succeeded, (await observer.ExecuteAsync(command, timeout.Token)).Outcome);
        ManagementReply other = await observer.InspectAsync(Command(clusterId, b, "workflows.read", NodeValue.FromObject(save.Definition.Id)), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, other.Outcome);
        Assert.Equal(save.Definition.Name, other.Value!.Json.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
    }

    /// <summary>UI側で明示する元の実体と要求IDを固定します。</summary>
    /// <returns>そのまま再送できる要求です。</returns>
    private static ManagementCommand Command(string clusterId, PeerInfo peer, string operation, NodeValue value) => new(Guid.NewGuid(), clusterId, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), operation, value);
}
