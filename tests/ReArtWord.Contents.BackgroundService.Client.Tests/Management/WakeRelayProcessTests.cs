using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>別プロセスのCore相当窓口へ実通信し、送信後の確認不能でも再送しません。</summary>
public sealed class WakeRelayProcessTests
{
    /// <summary>応答前にUI側を取り消し、同じIDの照会から最終結果だけを回収します。</summary>
    /// <returns>実UDPのMagic Packetが一回だけ送られることです。</returns>
    [Fact]
    public async Task CancellationAfterSendDoesNotRepeatMagicPackets()
    {
        string root = Directory.CreateTempSubdirectory("contents-wake-process-").FullName;
        Process? child = null;
        try
        {
            using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
            int destinationPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            int port;
            using (UdpClient reservation = new(0))
            {
                port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
            }
            string cluster = Guid.NewGuid().ToString("N"), configuration = Path.Combine(root, "fixture.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { directory = root, cluster, port, destinationPort }));
            string build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            string suffix = string.Empty;
            if (OperatingSystem.IsWindows())
            {
                suffix = ".exe";
            }
            string executable = Path.GetFullPath("../../../../ReArtWord.Contents.BackgroundService.Unit.Tests/Management/FixtureHost/bin/" + build + "/net10.0/ContentsDesktopFixture" + suffix, AppContext.BaseDirectory);
            ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("wake-relay");
            start.ArgumentList.Add(configuration);
            child = Process.Start(start) ?? throw new IOException("Relay fixture did not start.");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            ObserverConnection connection = new(Path.Combine(root, "client"));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
            string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(IPAddress.Loopback))).Id;
            await connection.SelectAsync(new(nic, "127.0.0.1", "127.255.255.255", port), timeout.Token);
            while (!connection.GetClusters().SelectMany(item => item.Peers).Any(peer => peer.Status == EPeerStatus.Ready))
            {
                Assert.False(child.HasExited, "Relay fixture exited before readiness.");
                await Task.Delay(25, timeout.Token);
            }
            using ClientCommandLedger ledger = new(Path.Combine(root, "client"));
            using WakeHostCache cache = new(root, connection.GetClusters, new UnusedRemote());
            DeviceHostTarget target = new("offline-host", cluster, "offline-peer", Guid.NewGuid());
            DeviceSnapshot snapshot = DeviceSnapshots.Create(new DeviceWakeState(new(true, []),
                new([new(Guid.NewGuid().ToString(), "test-interface", "02-11-22-33-44-55", true)], [new("127.0.0.2", 8)])));
            await cache.ObserveAsync(target, snapshot, cache.BeginRead(), timeout.Token);
            WakeHostDispatch dispatch = new(connection, cache, ledger);
            HostWakeStart request = new(Guid.NewGuid(), target.HostId);
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task<HostWakeResult> pending = dispatch.SendAsync(request, caller.Token);
            try
            {
                UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token);
                Assert.Equal(102, packet.Buffer.Length);
                await caller.CancelAsync();
                Assert.Equal(EOutcome.Unknown, (await pending.ConfigureAwait(true)).Outcome);
            }
            finally
            {
                await Task.WhenAll(caller.CancelAsync(), pending).ConfigureAwait(true);
            }
            Assert.Equal(EOutcome.Unknown, (await dispatch.SendAsync(request, timeout.Token)).Outcome);
            await File.WriteAllTextAsync(Path.Combine(root, "release"), string.Empty, timeout.Token);
            HostWakeResult final;
            do
            {
                await Task.Delay(50, timeout.Token);
                final = await dispatch.SendAsync(request, timeout.Token);
            } while (final.Outcome == EOutcome.Unknown);
            Assert.Equal(EOutcome.Succeeded, final.Outcome);
            Assert.Equal(1, final.PacketsSent);
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(root, "sent"), timeout.Token));
            Assert.Equal(0, receiver.Available);
            Assert.Equal(request.OperationId, final.OperationId);
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "release"), string.Empty);
                await File.WriteAllTextAsync(Path.Combine(root, "stop"), string.Empty);
            }
            finally
            {
                await StopAsync(child).ConfigureAwait(true);
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>終了要求を書き込めなかった場合も、試験用プロセスの終了まで待ちます。</summary>
    /// <returns>子プロセスの終了とハンドルの解放です。</returns>
    private static async Task StopAsync(Process? child)
    {
        if (child is null)
        {
            return;
        }

        using (child)
        {
            // 通常終了の猶予を過ぎた場合は強制終了し、回収が済むまで待つ。
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(3));
            try
            {
                await ProcessCompletion.WaitAsync(child, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                }
                await ProcessCompletion.WaitAsync(child).ConfigureAwait(false);
            }
        }
    }

    /// <summary>試験のプロフィールは明示投入し、OS設定へは接続しません。</summary>
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
