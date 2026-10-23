using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>初回だけの暫定参加と、明示離脱のローカル回復を検証します。</summary>
public sealed class ServiceReadinessTests
{
    /// <summary>通常巡回の読取り失敗も既存の障害境界で扱い、確認済みの有効設定を維持します。</summary>
    /// <returns>初期化完了後の確認失敗を実行停止へ拡大しないことです。</returns>
    [Fact]
    public async Task ReconciliationFailurePreservesConfirmedOperationalStateAsync()
    {
        using Media.MediaTestFiles files = new();
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        FailingRepository? failing = null;
        ManagementTestPeer peer = new(files.Root, Guid.NewGuid().ToString("N"), "peer", port, true, repository => failing = new(repository));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        await peer.Runtime.Maintenance.LoadAsync();
        await peer.Cluster.StartAsync();
        Assert.Equal(EOutcome.Succeeded, (await peer.Runtime.Readiness.InitializeOperationalAsync(true)).Outcome);
        string revision = peer.Runtime.Operational.Capture()!.Revision;
        failing!.Reading = static (scope, _) =>
        {
            if (string.Equals(scope, ClusterConfigurationStore.ActivationScope, StringComparison.Ordinal))
            {
                throw new IOException("The activation cannot be read.");
            }
        };
        Assert.Equal(EOutcome.Unknown, (await peer.Runtime.Readiness.InitializeOperationalAsync(true, reconcileOnly: true)).Outcome);
        Assert.Equal(revision, peer.Runtime.Operational.Capture()!.Revision);
        Assert.True(await peer.Runtime.Readiness.CheckAsync());
    }

    /// <summary>初期保存のどの段階でも未確認Peerを実行候補のまま残しません。</summary>
    /// <returns>失敗時の撤回と確認済み共有版のローカル再採用です。</returns>
    [Theory]
    [InlineData("snapshots", false)]
    [InlineData("peer:peer:operational-effective", true)]
    public async Task FailedInitialSeedRevokesProvisionalReady(string failingScope, bool canPrepare)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-readiness-" + Guid.NewGuid().ToString("N"));
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        FailingRepository? failing = null;
        ManagementTestPeer peer = new(root, Guid.NewGuid().ToString("N"), "peer", port, true, repository => failing = new(repository) { FailScope = failingScope });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        await peer.Runtime.Maintenance.LoadAsync();
        await peer.Cluster.StartAsync();
        Assert.Equal(EPeerStatus.Ready, peer.Runtime.Status);
        Assert.NotEqual(EOutcome.Succeeded, (await peer.Runtime.Readiness.InitializeOperationalAsync(true)).Outcome);
        Assert.NotNull(peer.Runtime.StartupProblem);
        await ManualSafetyFixture.WaitAsync(() => peer.Runtime.Status == EPeerStatus.Unavailable);
        Assert.Null(peer.Cluster.CoordinatorId);
        Assert.Null(peer.Runtime.Operational.Capture());
        failing!.FailScope = null;
        if (canPrepare)
        {
            await ManualSafetyFixture.WaitAsync(() => peer.Runtime.Status == EPeerStatus.Ready);
            Assert.NotNull(peer.Runtime.Operational.Capture());
            failing.FailScope = failingScope;
            Assert.True(await peer.Runtime.Readiness.CheckAsync());
        }
        else
        {
            Assert.False(await peer.Runtime.Readiness.CheckAsync());
        }
    }

    /// <summary>一時必要条件が戻っても通常再参加せず、元離脱実体のローカル状態を維持します。</summary>
    /// <returns>利用条件falseからtrueへの実監視回復です。</returns>
    [Fact]
    public async Task DetachedAvailabilityRecoversWithoutRejoiningOrChangingManualScope()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.Peers["a"].DetachAsync();
        await fixture.Safety["a"].ChangeAsync(new(Guid.NewGuid(), "workflow", "node", true));
        Guid instance = fixture.Peers["a"].LocalInstanceId;
        fixture.Peers["a"].ReadinessCheck = token => ValueTask.FromResult(false);
        await ManualSafetyFixture.WaitAsync(() => Assert.Single(fixture.Peers["a"].GetPeers()).Status == EPeerStatus.Unavailable);
        await fixture.Safety["a"].RefreshAsync();
        Assert.True(Assert.Single(fixture.Safety["a"].Current).LocalOnly);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Stopped);
        fixture.Peers["a"].ReadinessCheck = token => ValueTask.FromResult(true);
        await ManualSafetyFixture.WaitAsync(() => Assert.Single(fixture.Peers["a"].GetPeers()).Status == EPeerStatus.Detached);
        Assert.Equal(instance, fixture.Peers["a"].LocalInstanceId);
        Assert.True(fixture.Peers["a"].IsDetached);
        Assert.Null(fixture.Peers["a"].CoordinatorId);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Safety["a"].ChangeAsync(new(Guid.NewGuid(), "workflow", "node", false))).Outcome);
        Assert.Empty(await fixture.Repositories["a"].ListAsync("execution:safety"));
    }
}
