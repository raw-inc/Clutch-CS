using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>ClientHostの保存・結果照会を別プロセスへ通し、停止中Hostの候補と旧要求の固定を確認します。</summary>
public sealed class HostPowerScheduleRemoteTests
{
    /// <summary>NIC観測が後から変わっても、同じ操作IDでは保存を作り直さず元結果だけを返します。</summary>
    /// <returns>オフライン編集、所属クラスタ、NIC初期値と保存一回の検証です。</returns>
    [Fact]
    public async Task OfflineHostCanSaveAndQueryOriginalResultWithoutResendingAsync()
    {
        string root = Directory.CreateTempSubdirectory("contents-power-client-").FullName;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
            int port;
            using (UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
            }
            string cluster = "power-client-" + Guid.NewGuid().ToString("N");
            string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(IPAddress.Loopback))).Id;
            UdpObserver observer = new(new(Guid.NewGuid().ToString("N"), new(nic, "127.0.0.1", "127.255.255.255", port)));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
            await observer.StartAsync(timeout.Token);
            PowerSchedulePeer peer = new(root, cluster, "relay", port, 0, observer, true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
            peer.Start();
            await peer.ReadyAsync(cancellationToken: timeout.Token);
            ObserverConnection connection = new(Path.Combine(root, "client"));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
            await connection.SelectAsync(new(nic, "127.0.0.1", "127.255.255.255", port), timeout.Token);
            while (!connection.GetClusters().Any(item => item.CoordinatorCandidates.Count == 1))
            {
                await Task.Delay(25, timeout.Token);
            }
            using ClientCommandLedger ledger = new(Path.Combine(root, "client"));
            using WakeHostCache cache = new(root, connection.GetClusters, new UnusedRemote());
            DeviceHostTarget target = new("offline", cluster, "gone", Guid.NewGuid());
            DeviceWakeState state = new(new(true, []), new([new(Guid.NewGuid().ToString(), "test", "02-11-22-33-44-55", true)], []));
            await cache.ObserveAsync(target, DeviceSnapshots.Create(state), cache.BeginRead(), timeout.Token);
            HostPowerScheduleRemote remote = new(connection, ledger, cache);
            Assert.Equal(cluster, Assert.Single(await remote.ClustersAsync(target.HostId, timeout.Token)));
            DeviceHostInfo offline = Assert.Single(await remote.IncludeOfflineAsync([], timeout.Token));
            Assert.Null(offline.Capabilities);
            Assert.Equal(Guid.Empty, offline.Target.InstanceId);
            HostPowerScheduleSaveRequest request = new(Guid.NewGuid(), cluster, target.HostId, null, []);
            HostPowerScheduleSaveResult first = await remote.SaveAsync(request, timeout.Token);
            Assert.Equal(EOutcome.Succeeded, first.Outcome);
            Assert.True(first.View!.WakeProfileAvailable);
            await cache.ObserveAsync(target, DeviceSnapshots.Create(state with { Selection = new(false, []) }), cache.BeginRead(), timeout.Token);
            HostPowerScheduleSaveResult repeated = await remote.SaveAsync(request, timeout.Token);
            Assert.Equal(EOutcome.Succeeded, repeated.Outcome);
            Assert.Equal(first.View.Revision, repeated.View!.Revision);
            Assert.Equal(first.View.Revision, (await remote.ReadAsync(new(cluster, target.HostId), timeout.Token)).Revision);
            HostPowerScheduleSaveResult mismatch = await remote.SaveAsync(request with { HostId = "different" }, timeout.Token);
            Assert.Equal(EOutcome.NotExecuted, mismatch.Outcome);
            Assert.Equal("powerSchedule.invalid", mismatch.Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>試験でNICの実設定へ接続しない境界です。</summary>
    private sealed class UnusedRemote : IDeviceRemote
    {
        /// <summary>OS照会を禁止します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceSnapshot> ReadAsync(DeviceTargetRead request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>OS変更を禁止します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceChangeResult> ApplyAsync(DevicePlannedChange change, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>OS変更の照会を禁止します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceChangeResult?> ResultAsync(DevicePlannedChange change, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
