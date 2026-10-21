using ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>停止中・ClientHost再起動後も確認済みNICを保持し、定常照会では書き直しません。</summary>
public sealed class WakeHostCacheTests
{
    /// <summary>同じ内容の再観測ではディスクへ書かず、古い返信が新しい選択を上書きしません。</summary>
    /// <returns>保存・停止・再読込み後の同じ選択です。</returns>
    [Fact]
    public async Task SelectionSurvivesOfflineRestartWithoutRepeatedWritesOrStaleReplies()
    {
        string directory = Directory.CreateTempSubdirectory("contents-wake-cache-").FullName;
        try
        {
            string nic = Guid.NewGuid().ToString();
            DeviceHostTarget target = new("sleeping-host", "cluster", "peer", Guid.NewGuid());
            DeviceWakeInventory inventory = new([new(nic, "Ethernet", "02-11-22-33-44-55", true)], [new("192.0.2.10", 24)]);
            DeviceSnapshot original = DeviceSnapshots.Create(new DeviceWakeState(new(true, []), inventory));
            using (WakeHostCache cache = new(directory, () => [], new NoRemote()))
            {
                await cache.ObserveAsync(target, original, cache.BeginRead());
                string path = Path.Combine(directory, "wake-hosts.json");
                DateTime marker = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, marker);
                await cache.ObserveAsync(target, original, cache.BeginRead());
                Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
                long oldOrder = cache.BeginRead();
                DeviceSnapshot disabled = DeviceSnapshots.Create(new DeviceWakeState(new(false, []), inventory));
                await cache.ObserveAsync(target, disabled, cache.BeginRead());
                await cache.ObserveAsync(target, original, oldOrder);
                Assert.Empty(Assert.Single(await cache.ReadAsync()).State.Selected());
            }
            using WakeHostCache restarted = new(directory, () => [], new NoRemote());
            HostWakeProfile saved = Assert.Single(await restarted.ReadAsync());
            Assert.Equal(target.HostId, saved.HostId);
            Assert.Equal(["cluster"], saved.Clusters);
            Assert.Empty(saved.State.Selected());
            Assert.Single(saved.State.Inventory.Interfaces);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>ネットワーク一致を優先し、名前解決やARPへ依存しません。</summary>
    [Fact]
    public void RelayRankingUsesConfirmedIpv4Networks()
    {
        HostWakeProfile target = new("sleeping", "revision", new(new(true, []), new([], [new("192.0.2.10", 24)])), ["cluster"], DateTimeOffset.UtcNow);
        PeerInfo local = new("local", Guid.NewGuid(), "relay", "192.0.2.20", 1, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);
        PeerInfo remote = local with { Address = "198.51.100.20" };
        Assert.InRange(WakeHostDispatch.NetworkScore(target, local, []), 1, int.MaxValue);
        Assert.Equal(0, WakeHostDispatch.NetworkScore(target, remote, []));
        Assert.False(WakeHostDispatch.InSubnet("example.invalid", new("192.0.2.10", 24)));
        Assert.False(WakeHostDispatch.InSubnet("192.0.2.20", new("192.0.2.10", 32)));
    }

    /// <summary>保存試験から本番へ照会・変更しない通信境界です。</summary>
    private sealed class NoRemote : IDeviceRemote
    {
        /// <summary>この試験では明示観測だけを使います。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceSnapshot> ReadAsync(DeviceTargetRead request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>設定変更を禁止します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceChangeResult> ApplyAsync(DevicePlannedChange change, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>設定変更の照会を禁止します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceChangeResult?> ResultAsync(DevicePlannedChange change, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
