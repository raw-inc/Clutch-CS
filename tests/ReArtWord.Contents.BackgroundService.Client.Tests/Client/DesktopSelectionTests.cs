using ReArtWord.Contents.BackgroundService.ClientHost.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ホスト選択とクラスタ自動選定が明示指定を越えないことを確認します。</summary>
public sealed class DesktopSelectionTests
{
    /// <summary>同じホストを持つクラスタを安定順で選び、明示指定を優先します。</summary>
    [Fact]
    public void AutomaticAndExplicitClustersChooseTheRequestedHost()
    {
        DiscoveredCluster[] clusters = [Cluster("b", Peer("b", "target")), Cluster("a", Peer("a", "target"), Peer("other", "elsewhere"))];
        Assert.Equal("a", DesktopRoutes.Select(clusters, new DesktopTarget("target")).Cluster);
        Assert.Equal("b", DesktopRoutes.Select(clusters, new DesktopTarget("target", "b")).Cluster);
        Assert.Throws<InvalidOperationException>(() => DesktopRoutes.Select(clusters, new DesktopTarget("target", "missing")));
        Assert.Throws<InvalidOperationException>(() => DesktopRoutes.Select(clusters, new DesktopTarget("missing")));
    }

    /// <summary>同一peer名の複数実体や不参加peerへ入力を送りません。</summary>
    [Fact]
    public void AmbiguousAndOfflineInstancesAreRejected()
    {
        DiscoveredCluster[] clusters = [Cluster("a", Peer("same", "target"), Peer("same", "target"))];
        Assert.Throws<InvalidOperationException>(() => DesktopRoutes.Select(clusters, new DesktopTarget("target")));
        clusters = [Cluster("a", Peer("offline", "target") with { Status = EPeerStatus.Unavailable })];
        Assert.Throws<InvalidOperationException>(() => DesktopRoutes.Select(clusters, new DesktopTarget("target")));
    }

    /// <summary>指定したpeerだけを含む観測結果です。</summary>
    /// <returns>固定した観測です。</returns>
    private static DiscoveredCluster Cluster(string name, params PeerInfo[] peers) => new(name, peers, [], DateTimeOffset.UtcNow);

    /// <summary>別々の実体を持つ参加中のpeerです。</summary>
    /// <returns>選択試験用の実体です。</returns>
    private static PeerInfo Peer(string name, string host) => new(name, Guid.NewGuid(), host, "127.0.0.1", 47740, EPeerStatus.Ready, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
