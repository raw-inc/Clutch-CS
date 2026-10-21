using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.DesktopFixture;

/// <summary>実クラスタと実SQLiteを使用し、電源操作だけを記録、WoLだけをループバック送信へ置換します。</summary>
internal static class PowerScheduleScenario
{
    /// <summary>通常サービスの設定・電源・NICへ触れず、試験専用の管理窓口を公開します。</summary>
    /// <returns>所有したクラスタ輸送の終了です。</returns>
    internal static async Task RunAsync(string configuration, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(configuration, cancellationToken).ConfigureAwait(false));
        JsonElement settings = document.RootElement;
        string root = settings.GetProperty("directory").GetString()!, clusterId = settings.GetProperty("cluster").GetString()!;
        string id = settings.GetProperty("peer").GetString()!, host = settings.GetProperty("host").GetString()!;
        int port = settings.GetProperty("port").GetInt32(), destination = settings.GetProperty("destinationPort").GetInt32();
        bool initialize = settings.GetProperty("initialize").GetBoolean();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(3));
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        int priority = initialize ? 10
                                  : 0;
        PeerCluster peer = new(new(clusterId, id, host, "127.0.0.1", "127.255.255.255", port, priority, initialize, root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(false);
        ControlledClock clock = new();
        HostPowerScheduleStore store = new(peer, repository, clock);
        HostPowerScheduler scheduler = new(store, peer, repository, host, _ => Task.FromResult(new DeviceWakeState(new(true, []), new([], []))), new FixtureLog(root), clock);
        HostPowerController controller = new(host, peer.LocalInstanceId, () => peer.GetPeers().Single(item => item.InstanceId == peer.LocalInstanceId).Status,
            async (_, token) =>
            {
                int admitted = (await repository.ObserveAsync(HostPowerScheduleStore.Occurrences, new Dictionary<string, string>(), cancellationToken: token).ConfigureAwait(false)).Count;
                await File.AppendAllTextAsync(Path.Combine(root, "power-events.jsonl"), JsonSerializer.Serialize(new { peer = id, admitted, at = DateTimeOffset.UtcNow }) + "\n", token).ConfigureAwait(false);
                return NodeResult.Success();
            }, due => HostPowerScheduleLatch.Claim(Path.Combine(root, "power-latch"), due), clock);
        WakeOnLanSender sender = new(() => [(IPAddress.Loopback, IPAddress.Loopback, 0)], destination);
        ManagementRegistry registry = new();
        registry.ReadValue("test.status", _ => new { peer.LocalInstanceId, peer.CoordinatorId, peers = peer.GetPeers(), processId = Environment.ProcessId });
        registry.Read("host.power-schedules.read", async (body, token) => await store.ReadAsync(ManagementRegistry.Body<HostPowerScheduleReadRequest>(body), token).ConfigureAwait(false));
        registry.Write("host.power-schedules.save", async (body, token) => await store.SaveAsync(ManagementRegistry.Body<HostPowerScheduleSaveRequest>(body), token).ConfigureAwait(false));
        registry.Write("host.power", async (body, token) => await controller.ExecuteAsync(ManagementRegistry.Body<HostPowerRequest>(body), token).ConfigureAwait(false));
        registry.Write("host.wake", async (body, token) => await sender.SendAsync(ManagementRegistry.Body<HostWakeRequest>(body), token).ConfigureAwait(false));
        registry.Transient("test.tick", async (body, token) =>
        {
            clock.Now = ManagementRegistry.Body<DateTimeOffset>(body);
            await scheduler.TickAsync(token).ConfigureAwait(false);
            return true;
        });
        _ = new PeerManagementEndpoint(peer, registry.ExecuteAsync, registry.IsReadOnly, registry.IsTransient);
        await peer.StartAsync(lifetime.Token).ConfigureAwait(false);
        string stop = Path.Combine(root, "stop");
        while (!File.Exists(stop))
        {
            await Task.Delay(25, lifetime.Token).ConfigureAwait(false);
        }
    }

    /// <summary>OSやクラスタ輸送の時計を変えず、予定観測の時刻だけ進めます。</summary>
    private sealed class ControlledClock : TimeProvider
    {
        /// <summary>次の観測時刻です。</summary>
        internal DateTimeOffset Now
        {
            get; set;
        } = DateTimeOffset.UtcNow;

        /// <summary>予定処理へ固定時刻を渡します。</summary>
        /// <returns>指定したUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>試験用のログをファイルへ保存します。</summary>
    private sealed class FixtureLog(string root) : IWorkflowLog
    {
        /// <summary>一行ずつ試験用ファイルへ書き出します。</summary>
        /// <returns>今回の記録の完了です。</returns>
        public async ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
            => await File.AppendAllTextAsync(Path.Combine(root, "schedule-log.jsonl"), JsonSerializer.Serialize(entry) + "\n", cancellationToken).ConfigureAwait(false);
    }
}
