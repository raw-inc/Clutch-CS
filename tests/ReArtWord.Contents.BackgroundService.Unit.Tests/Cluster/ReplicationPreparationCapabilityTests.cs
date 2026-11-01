using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>同じPeerでも起動実体が変われば圧縮対応を再確認し、旧版への切替を取り違えません。</summary>
public sealed class ReplicationPreparationCapabilityTests
{
    /// <summary>実UDPの参照応答を起動実体ごとに一度だけ使い、旧実体の対応情報を再利用しません。</summary>
    /// <returns>通常形式へ戻る判断と照会の重複抑止です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRechecksSupportAndExplicitLegacyResponseIsCachedAsync(bool dropFirstReply)
    {
        using MediaTestFiles files = new();
        string clusterId = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        SqliteRepository repository = new(Path.Combine(files.Root, "source.db"));
        await using PeerCluster source = LargeSynchronizationTests.Create(repository, files.Root, clusterId, "source", port, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await source.StartAsync(timeout.Token).ConfigureAwait(true);
        ReplicationPreparationCapabilities capabilities = new(source);
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        foreach (int maximum in new[] { 1048576, 0 })
        {
            Guid instance = Guid.NewGuid();
            ConcurrentQueue<Guid> probes = new();
            UdpTransport remote = new(new("remote", "127.0.0.1", "127.255.255.255", port, clusterId), instance, TimeProvider.System);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)remote).ConfigureAwait(true);
            remote.Received = async (message, _, token) =>
            {
                string inspection = "inspect";
                if (!string.Equals(message.Kind, inspection, StringComparison.Ordinal))
                {
                    return;
                }
                probes.Enqueue(message.Id);
                if (probes.Count == 1 && dropFirstReply)
                {
                    return;
                }
                ClusterResponse result = new(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable");
                if (0 < maximum)
                {
                    result = new(EOutcome.Succeeded, NodeValue.FromObject(maximum));
                }
                await remote.SendAsync(WireMessage.Reply(message, "remote", instance, JsonSerializer.SerializeToElement(result)), token).ConfigureAwait(false);
            };
            remote.Initialize(network);
            remote.Start();
            PeerInfo target = new("remote", instance, "remote-host", "127.0.0.1", port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Assert.Equal(maximum, await capabilities.MaximumBytesAsync(target, timeout.Token).ConfigureAwait(true));
            Assert.Equal(maximum, await capabilities.MaximumBytesAsync(target, timeout.Token).ConfigureAwait(true));

            // 応答が届くまでの再送と、同じ実体への新しい照会を区別します。
            Assert.Single(probes.Distinct());
            if (dropFirstReply)
            {
                Assert.True(1 < probes.Count);
            }
        }
    }
}
