using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>取消記録の到着順と実際の矛盾を、UDP同期の応答と参加状態で区別します。</summary>
public sealed class RoutingCancellationSynchronizationTests
{
    /// <summary>準備前の取消は同期でき、異なる要求の取消は応答を返して拒否します。</summary>
    /// <returns>実UDP、実保存、Readyの境界を含む検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRecordsReturnAConfirmedSynchronizationResult(bool differentRequest)
    {
        using MediaTestFiles firstFiles = new();
        using MediaTestFiles secondFiles = new();
        IDurableRepository firstStore = await RepositoryFactory.CreateAsync(firstFiles.Root, "first", new());
        IDurableRepository secondStore = await RepositoryFactory.CreateAsync(secondFiles.Root, "second", new());
        Guid operation = Guid.NewGuid();
        await firstStore.PrepareAsync(operation, [new("workflow:cancel", "value", null, NodeValue.FromObject(1))]);
        await firstStore.AbortPreparedAsync(operation);
        if (differentRequest)
        {
            await secondStore.PrepareAsync(operation, [new("workflow:cancel", "value", null, NodeValue.FromObject(2))]);
        }
        await secondStore.AbortPreparedAsync(operation);
        string firstReceipt = ContractJson.Hash(await firstStore.ReadReceiptAsync(operation));
        string secondReceipt = ContractJson.Hash(await secondStore.ReadReceiptAsync(operation));
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        await using PeerCluster first = Create(firstStore, firstFiles.Root, cluster, "first", port, true);
        await using PeerCluster second = Create(secondStore, secondFiles.Root, cluster, "second", port, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token);
        await firstStore.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "shared", null, NodeValue.FromObject(42))], timeout.Token);
        await second.StartAsync(timeout.Token);
        if (differentRequest)
        {
            string reason = "Sync.InvalidSnapshot";
            while (!string.Equals(second.SynchronizationProblem, reason, StringComparison.Ordinal))
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.Equal(EPeerStatus.Syncing, second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status);
            Assert.Null(await secondStore.ReadAsync("workflows", "shared", timeout.Token));
        }
        else
        {
            while (second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status != EPeerStatus.Ready || second.SynchronizationProblem is not null)
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.Equal(42, (await secondStore.ReadAsync("workflows", "shared", timeout.Token))!.Value!.Json.GetInt32());
        }
        Assert.Equal(firstReceipt, ContractJson.Hash(await firstStore.ReadReceiptAsync(operation, timeout.Token)));
        Assert.Equal(secondReceipt, ContractJson.Hash(await secondStore.ReadReceiptAsync(operation, timeout.Token)));
    }

    /// <summary>配送待ちを短縮した実Peerを、個別の保存先で構成します。</summary>
    /// <returns>起動前のPeerです。</returns>
    private static PeerCluster Create(IDurableRepository repository, string root, string cluster, string peer, int port, bool fresh) => new(
        new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromSeconds(1) }, repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
}
