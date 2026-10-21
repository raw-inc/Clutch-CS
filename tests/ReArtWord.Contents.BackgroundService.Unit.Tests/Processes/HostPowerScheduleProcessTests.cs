using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>別プロセス間の実通信で予定の共有・受付・再起動を検証し、実電源は操作しません。</summary>
[Collection("Performance")]
public sealed class HostPowerScheduleProcessTests
{
    /// <summary>全予定を共有してから対象を操作し、送信元を最後にし、再起動後も同じ回を送りません。</summary>
    /// <returns>別PCへの要求、同クラスタのWoL代送と永続重複抑止です。</returns>
    [Fact]
    public async Task DistributedSchedulesAreAdmittedBeforeShutdownAndDoNotReplayAsync()
    {
        using MediaTestFiles files = new();
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int destination = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        int port;
        using (UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }
        string cluster = "power-" + Guid.NewGuid().ToString("N");
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(IPAddress.Loopback))).Id;
        UdpObserver observer = new(new(Guid.NewGuid().ToString("N"), new(nic, "127.0.0.1", "127.255.255.255", port)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(100));
        await observer.StartAsync(timeout.Token);
        PowerSchedulePeer a = new(files.Root, cluster, "a", port, destination, observer, true);
        PowerSchedulePeer b = new(files.Root, cluster, "b", port, destination, observer, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable aLifetime = a.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable bLifetime = b.ConfigureAwait(true);
        a.Start();
        await a.ReadyAsync(cancellationToken: timeout.Token);
        b.Start();
        await b.ReadyAsync(cancellationToken: timeout.Token);
        DateTimeOffset due = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds());
        string time = due.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        HostPowerSchedule shutdown = new(Guid.NewGuid(), EHostPowerScheduleAction.Shutdown, time);
        HostPowerSchedule wake = new(Guid.NewGuid(), EHostPowerScheduleAction.Wake, time);
        HostPowerSchedule onlineWake = wake with { Id = Guid.NewGuid() };
        HostPowerScheduleSaveResult first = await SaveAsync(a, cluster, "host-a", [shutdown], timeout.Token);
        HostPowerScheduleSaveResult second = await SaveAsync(a, cluster, "host-b", [shutdown with { Id = Guid.NewGuid() }, onlineWake], timeout.Token);
        DeviceWakeState state = new(new(true, []), new([new(Guid.NewGuid().ToString(), "loopback-fixture", "02-11-22-33-44-55", true)], [new("127.0.0.2", 8)]));
        HostWakeProfile profile = new("offline", DeviceSnapshots.Create(state).Revision, state, [cluster], DateTimeOffset.UtcNow);
        HostPowerScheduleSaveResult offline = await a.SendAsync<HostPowerScheduleSaveResult>("host.power-schedules.save",
            new HostPowerScheduleSaveRequest(Guid.NewGuid(), cluster, "offline", null, [wake], profile), write: true, cancellationToken: timeout.Token);
        Assert.Equal(EOutcome.Succeeded, offline.Outcome);
        Assert.True(offline.View!.Active);
        Assert.True(offline.View.WakeProfileAvailable);
        HostPowerScheduleView observed = await b.SendAsync<HostPowerScheduleView>("host.power-schedules.read", new HostPowerScheduleReadRequest(cluster, "offline"), cancellationToken: timeout.Token);
        Assert.Equal(offline.View.Revision, observed.Revision);
        Assert.True(observed.Active);
        await TickAsync(a, due.AddSeconds(-1), timeout.Token);
        await TickAsync(b, due.AddSeconds(-1), timeout.Token);
        await TickAsync(b, due, timeout.Token);
        await TickAsync(a, due, timeout.Token);
        UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token);
        Assert.Equal(WakeOnLanSender.Packet("02-11-22-33-44-55"), packet.Buffer);
        JsonElement local = ReadEvent(a), remote = ReadEvent(b);
        Assert.Equal(4, local.GetProperty("admitted").GetInt32());
        Assert.Equal(4, remote.GetProperty("admitted").GetInt32());
        Assert.True(remote.GetProperty("at").GetDateTimeOffset() <= local.GetProperty("at").GetDateTimeOffset());
        HostPowerScheduleView status = await a.SendAsync<HostPowerScheduleView>("host.power-schedules.read", new HostPowerScheduleReadRequest(cluster, "host-b"), cancellationToken: timeout.Token);
        Assert.Equal(EOutcome.Succeeded, status.Status[0].Outcome);
        Assert.Equal("wake.alreadyOnline", status.Status[1].Reason);
        Assert.Equal(EOutcome.NotExecuted, status.Status[1].Outcome);
        await TickAsync(a, due.AddSeconds(1), timeout.Token);
        Assert.Equal(0, receiver.Available);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(a.DirectoryPath, "power-events.jsonl"), timeout.Token));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(b.DirectoryPath, "power-events.jsonl"), timeout.Token));

        // 全processを止めてもSQLiteの受付位置を保持し、同じ時刻境界を再観測して二重実行しません。
        Guid oldA = a.Current!.InstanceId, oldB = b.Current!.InstanceId;
        await b.StopAsync(timeout.Token);
        await a.StopAsync(timeout.Token);
        a.Start();
        await a.ReadyAsync(oldA, timeout.Token);
        b.Start();
        await b.ReadyAsync(oldB, timeout.Token);
        await TickAsync(a, due.AddSeconds(-1), timeout.Token);
        await TickAsync(b, due.AddSeconds(-1), timeout.Token);
        await TickAsync(b, due, timeout.Token);
        await TickAsync(a, due, timeout.Token);
        Assert.Equal(0, receiver.Available);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(a.DirectoryPath, "power-events.jsonl"), timeout.Token));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(b.DirectoryPath, "power-events.jsonl"), timeout.Token));
        Assert.NotNull(first.View);
        Assert.NotNull(second.View);
    }

    /// <summary>元の設定と同じAPIで一回の予定を保存します。</summary>
    /// <returns>共有保存と有効化が成功した設定です。</returns>
    private static async Task<HostPowerScheduleSaveResult> SaveAsync(PowerSchedulePeer peer, string cluster, string host, HostPowerSchedule[] schedules, CancellationToken cancellationToken = default)
    {
        HostPowerScheduleSaveResult result = await peer.SendAsync<HostPowerScheduleSaveResult>("host.power-schedules.save",
            new HostPowerScheduleSaveRequest(Guid.NewGuid(), cluster, host, null, schedules), write: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(result.View!.Active);
        return result;
    }

    /// <summary>OSや輸送時計を変更せず、スケジューラーだけに時刻境界を観測させます。</summary>
    /// <returns>観測した回の処理完了です。</returns>
    private static async Task TickAsync(PowerSchedulePeer peer, DateTimeOffset time, CancellationToken cancellationToken = default)
        => Assert.True(await peer.SendAsync<bool>("test.tick", time, transient: true, cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>OS境界の代わりに生成した記録を読みます。</summary>
    /// <returns>一度だけ呼ばれた無害な処理です。</returns>
    private static JsonElement ReadEvent(PowerSchedulePeer peer)
    {
        string line = Assert.Single(File.ReadAllLines(Path.Combine(peer.DirectoryPath, "power-events.jsonl")));
        return JsonSerializer.Deserialize<JsonElement>(line);
    }
}
