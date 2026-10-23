using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>実UDP/TCPで、Readyと背景履歴完了を分離しても現在状態と一回性を保つことを確認します。</summary>
public sealed class StagedSynchronizationTests
{
    /// <summary>取得元の実体が消えても通常参加を維持し、同じPeerの再起動後に背景補完を続けます。</summary>
    /// <returns>実体を跨いだ未取得状態と、元の一回性を維持した再開です。</returns>
    [Fact]
    public async Task BackgroundHistoryResumesAfterItsSourceRestarts()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        using RepositoryRuntime sourceRuntime = await RepositoryFactory.CreateRuntimeAsync(sourceFiles.Root, "source", new());
        using RepositoryRuntime targetRuntime = await RepositoryFactory.CreateRuntimeAsync(targetFiles.Root, "target", new());
        IDurableRepository sourceStore = sourceRuntime.Repository;
        IDurableRepository targetStore = targetRuntime.Repository;
        for (int index = 0; index < 256; index++)
        {
            await sourceStore.AbortPreparedAsync(Guid.NewGuid());
        }
        string clusterId = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        TimeProvider clock = new HistoryClock();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await using PeerCluster target = Create(targetStore, targetFiles.Root, clusterId, "target", port, false, clock);
        RecoveryRepository incoming = RecoveryRepository.Create(targetStore)!;
        Guid originalInstance;
        await using (PeerCluster source = Create(sourceStore, sourceFiles.Root, clusterId, "source", port, true, clock))
        {
            originalInstance = source.LocalInstanceId;
            await source.StartAsync(timeout.Token);
            await target.StartAsync(timeout.Token);
            while (target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).Status != EPeerStatus.Ready)
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.True(0 < (await incoming.StatusAsync(timeout.Token)).Pending);
        }
        DateTimeOffset? readySince = target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).ReadySince;
        await Task.Delay(1000, timeout.Token);
        Assert.True(0 < (await incoming.StatusAsync(timeout.Token)).Pending);
        Assert.Equal(EPeerStatus.Ready, target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).Status);
        IDurableRepository restored = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        await using PeerCluster replacement = Create(restored, sourceFiles.Root, clusterId, "source", port, false, clock);
        await replacement.StartAsync(timeout.Token);
        Assert.NotEqual(originalInstance, replacement.LocalInstanceId);
        while (0 < (await incoming.StatusAsync(timeout.Token)).Pending)
        {
            Assert.Equal(EPeerStatus.Ready, target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).Status);
            await Task.Delay(100, timeout.Token);
        }
        Assert.Equal(readySince, target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).ReadySince);
        Assert.Equal(100, (await incoming.StatusAsync(timeout.Token)).ProgressPercent);
    }

    /// <summary>履歴取得完了を待たず参加し、背景取得中も新しい共有保存が成功します。</summary>
    /// <returns>先行Ready、現在値維持、稼働側の無中断と背景完了です。</returns>
    [Fact]
    public async Task ReadyPrecedesHistoryCompletionAndCurrentWritesRemainAvailable()
    {
        using MediaTestFiles firstFiles = new();
        using MediaTestFiles secondFiles = new();
        using RepositoryRuntime firstRuntime = await RepositoryFactory.CreateRuntimeAsync(firstFiles.Root, "first", new());
        using RepositoryRuntime secondRuntime = await RepositoryFactory.CreateRuntimeAsync(secondFiles.Root, "second", new());
        IDurableRepository firstStore = firstRuntime.Repository;
        IDurableRepository secondStore = secondRuntime.Repository;
        string clusterId = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        TimeProvider clock = new HistoryClock();
        await using PeerCluster first = Create(firstStore, firstFiles.Root, clusterId, "first", port, true, clock);
        await using PeerCluster second = Create(secondStore, secondFiles.Root, clusterId, "second", port, false, clock);
        await first.StartAsync();
        DateTimeOffset? readySince = first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).ReadySince;
        Guid firstOperation = Guid.Empty;
        string? version = null;
        for (int index = 0; index < 128; index++)
        {
            Guid id = Guid.NewGuid();
            if (index == 0)
            {
                firstOperation = id;
            }
            Assert.Equal(EOutcome.Succeeded, (await firstStore.ApplyBatchAsync(id,
                [new("workflow:history-test", "state", version, NodeValue.FromObject(new { Index = index, Padding = new string('x', 16384) }))])).Outcome);
            version = id.ToString("N");
        }

        // 参加先の起動・背景取得の期限は、履歴の準備後に計測します。
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await second.StartAsync(timeout.Token);
        while (second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status != EPeerStatus.Ready)
        {
            Assert.Equal(EPeerStatus.Ready, first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).Status);
            await Task.Delay(20, timeout.Token);
        }
        RecoveryRepository incoming = RecoveryRepository.Create(secondStore)!;
        Assert.True(0 < (await incoming.StatusAsync(timeout.Token)).Pending);
        Assert.Equal(127, (await secondStore.ReadAsync("workflow:history-test", "state", timeout.Token))!.Value!.Json.GetProperty("Index").GetInt32());
        Assert.Equal("Persistence.HistoryPending", (await secondStore.ReadReceiptAsync(firstOperation, timeout.Token))!.Reason);
        Guid update = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await first.ReplicateAsync(update,
            [new("workflow:history-test", "state", version, NodeValue.FromObject("live-change"))], timeout.Token)).Outcome);
        while (0 < (await incoming.StatusAsync(timeout.Token)).Pending)
        {
            Assert.Equal(EPeerStatus.Ready, first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).Status);
            Assert.Equal(EPeerStatus.Ready, second.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status);
            await Task.Delay(100, timeout.Token);
        }
        Assert.Equal(readySince, first.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).ReadySince);
        Assert.Equal("live-change", (await secondStore.ReadAsync("workflow:history-test", "state", timeout.Token))!.Value!.GetString());
        Assert.Equal(EOutcome.Succeeded, (await secondStore.ReadReceiptAsync(firstOperation, timeout.Token))!.Outcome);
    }

    /// <summary>WFエンジンを持たない保存・通信だけのPeerを用意します。</summary>
    /// <returns>ループバックだけへ接続する実Peerです。</returns>
    private static PeerCluster Create(IDurableRepository repository, string root, string id, string peer, int port, bool fresh, TimeProvider clock) => new(
        new ClusterOptions(id, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromSeconds(2) }, repository, clock)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>通常のタイマーで待機しながら、作成済みの履歴を直近区間の外へ置きます。</summary>
    private sealed class HistoryClock : TimeProvider
    {
        /// <summary>壁時計だけを2分進め、長時間の試験待ちを避けます。</summary>
        /// <returns>実時刻と同じ速度で進む履歴検証用時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(2);
    }
}
