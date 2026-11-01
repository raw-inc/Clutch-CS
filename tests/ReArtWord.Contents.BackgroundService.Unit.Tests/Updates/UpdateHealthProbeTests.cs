using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新後の探索期限・起動期限・呼び出し元キャンセルを区別します。</summary>
public sealed class UpdateHealthProbeTests
{
    /// <summary>遅れて現れる新実体だけを採用し、全体の期限とキャンセルでは待機を終えます。</summary>
    /// <returns>実UDPによる起動確認の結果です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DiscoveryRetriesStayWithinTheStartupDeadlineAsync(bool cancel, bool expire)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        ProcessIdentity identity = new(1234, DateTimeOffset.UtcNow);
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        target.Initialize(nic);
        int inspections = 0;
        target.Received = async (message, _, token) =>
        {
            Assert.Equal("inspect", message.Kind);
            Interlocked.Increment(ref inspections);
            ManagementReply reply = new(EOutcome.Succeeded, NodeValue.FromObject(new { peerId = "target", ready = true, instanceId = instance, processId = identity.ProcessId, processStartedAt = identity.StartedAt }));
            ClusterResponse response = new(EOutcome.Succeeded, NodeValue.FromObject(reply));
            await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
        };
        target.Start();
        int startTimeout = expire ? 100
                                  : 20000;
        UpdatesOptions options = new(StartTimeoutMilliseconds: startTimeout, InterfaceId: nic, LocalAddress: binding.BindAddress, ClusterId: cluster, BroadcastAddress: binding.BroadcastAddress, ManagementPort: port);
        LocalUpdateJob job = new(Guid.NewGuid(), "target", Guid.NewGuid(), identity, new("fixture", "win-x64", "fixture.exe", [], []), Path.GetTempPath(), options, []);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        Task<UpdateHealthResult> observation = UpdateHealthProbe.WaitAsync(job, null, deadline.Token);
        try
        {
            if (cancel)
            {
                await deadline.CancelAsync().ConfigureAwait(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation).ConfigureAwait(true);
                Assert.Equal(0, inspections);
                return;
            }
            if (expire)
            {
                UpdateHealthResult unavailable = await observation.ConfigureAwait(true);
                Assert.False(unavailable.Ready);
                Assert.Null(unavailable.InstanceId);
                Assert.Null(unavailable.Process);
                Assert.False(deadline.IsCancellationRequested);
                Assert.Equal(0, inspections);
                return;
            }

            // 探索一回の10秒を超えても、設定した起動期限内なら新実体を確認できます。
            await Task.Delay(TimeSpan.FromSeconds(11), deadline.Token).ConfigureAwait(true);
            long sequence = 0;
            while (!observation.IsCompleted)
            {
                PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), deadline.Token).ConfigureAwait(true);
                await Task.Delay(20, deadline.Token).ConfigureAwait(true);
            }
            UpdateHealthResult result = await observation.ConfigureAwait(true);
            Assert.True(result.Ready);
            Assert.Equal(instance, result.InstanceId);
            Assert.Equal(identity, result.Process);
            Assert.True(0 < inspections);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(true);
            await ((Task)observation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
}
