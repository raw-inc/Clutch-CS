using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>PC表示の永続化、明示削除、実体の再発見を確認します。</summary>
public sealed class DashboardHostStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "contents-hosts-" + Guid.NewGuid().ToString("N"));
    private readonly List<DiscoveredCluster> discovered = [];

    /// <summary>発見期限と再起動を越えて表示を保持し、削除後は古い標本で復活しません。</summary>
    /// <returns>保存と再読込の検証です。</returns>
    [Fact]
    public async Task OfflineHistorySurvivesReloadUntilExplicitRemovalAsync()
    {
        PeerInfo peer = Peer("host");
        discovered.Add(new("cluster", [peer], [], peer.LastSeen));
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, peer.LastSeen, [], InstanceId: peer.InstanceId, SelectedInterfaceId: "virtual-nic");
        DashboardPeerSnapshot snapshot = new("cluster", peer, peer.LastSeen, null, null, [sample]);
        using (DashboardHostStore first = new(directory, () => discovered))
        {
            await first.ObserveAsync([snapshot]);
            Assert.True(Assert.Single(await first.ReadAsync(null)).Online);
            Assert.False(await first.RemoveAsync(peer.HostId));
        }
        discovered.Clear();
        using DashboardHostStore reloaded = new(directory, () => discovered);
        DashboardHostPeer retained = Assert.Single(await reloaded.ReadAsync("cluster"));
        Assert.False(retained.Online);
        Assert.Equal(sample.Id, retained.Sample!.Id);
        Assert.Equal("virtual-nic", retained.Sample.SelectedInterfaceId);
        Assert.Empty(await reloaded.ReadAsync("other"));
        Assert.True(await reloaded.RemoveAsync(peer.HostId));
        await reloaded.ObserveAsync([snapshot]);
        Assert.Empty(await reloaded.ReadAsync(null));
        using DashboardHostStore again = new(directory, () => discovered);
        await again.ObserveAsync([snapshot]);
        Assert.Empty(await again.ReadAsync(null));
        PeerInfo live = peer with { InstanceId = Guid.NewGuid(), LastSeen = DateTimeOffset.UtcNow };
        discovered.Add(new("cluster", [live], [], live.LastSeen));
        await again.ObserveAsync([snapshot with { Peer = live, Samples = [] }]);
        Assert.Equal(live.InstanceId, Assert.Single(await again.ReadAsync(null)).Peer.InstanceId);
    }

    /// <summary>同じPeer名でもPCを上書きせず、複数クラスタの生存を確認して削除を拒否します。</summary>
    /// <returns>Host識別と共有PCの検証です。</returns>
    [Fact]
    public async Task HostIdentityAndEveryClusterDetermineAvailabilityAsync()
    {
        PeerInfo old = Peer("old-host") with { LastSeen = DateTimeOffset.UtcNow.AddDays(-1) };
        PeerInfo current = Peer("new-host");
        discovered.Add(new("cluster", [current], [], current.LastSeen));
        discovered.Add(new("other", [current], [], current.LastSeen));
        using DashboardHostStore store = new(directory, () => discovered);
        await store.ObserveAsync([new("cluster", old, null, null, null, []), new("cluster", current, null, null, null, []), new("other", current, null, null, null, [])]);
        IReadOnlyList<DashboardHostPeer> rows = await store.ReadAsync(null);
        Assert.Equal(3, rows.Count);
        Assert.False(Assert.Single(rows, row => string.Equals(row.Peer.HostId, old.HostId, StringComparison.Ordinal)).Online);
        discovered.RemoveAt(0);
        Assert.False(await store.RemoveAsync(current.HostId));
        Assert.True(await store.RemoveAsync(old.HostId));
        Assert.Equal(2, (await store.ReadAsync(null)).Count);
        Assert.False(await store.RemoveAsync("never-observed"));
    }

    /// <summary>書込不能を成功扱いせず、復旧後も削除前の表示を保持します。</summary>
    /// <returns>保存障害とメモリー巻戻しの検証です。</returns>
    [Fact]
    public async Task FailedRemovalRollsBackAndReportsPersistenceFailureAsync()
    {
        PeerInfo peer = Peer("offline");
        using DashboardHostStore store = new(directory, () => discovered);
        await store.ObserveAsync([new("cluster", peer, null, null, null, [])]);
        string pending = Path.Combine(directory, "dashboard-hosts.json.pending");
        Directory.CreateDirectory(pending);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.RemoveAsync(peer.HostId));
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(null));
        Directory.Delete(pending);
        await store.ObserveAsync([]);
        Assert.Single(await store.ReadAsync(null));
    }

    /// <summary>生存を観測したPC情報です。</summary>
    /// <returns>一意な実体です。</returns>
    private static PeerInfo Peer(string host) => new("peer", Guid.NewGuid(), host, "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);

    /// <summary>試験専用保存先を片付けます。</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
