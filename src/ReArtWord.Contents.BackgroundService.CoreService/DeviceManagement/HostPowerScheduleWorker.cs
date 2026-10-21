using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.DeviceManagement;

/// <summary>一つのCoreが所有する電源予定の巡回を、ブラウザーとWFから独立して継続します。</summary>
internal sealed class HostPowerScheduleWorker(ServiceRuntime runtime, HostPowerScheduleStore store, DeviceManagementService devices,
    IWorkflowLog log, SessionFailureBoundary boundary) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>所有する一回の処理を待ち、非致命的な失敗はCore終了へ伝えません。</summary>
    /// <returns>サービス停止までの監視です。</returns>
    // 抑制理由: BackgroundServiceの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BackgroundServiceの停止トークンを全I/Oへ渡します。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HostPowerScheduler scheduler = new(store, runtime.Cluster, runtime.Repository, runtime.Configuration.Cluster.HostId, async token =>
        {
            DeviceSnapshot snapshot = await devices.ReadAsync(new(EDeviceArea.WakeOnLan), token).ConfigureAwait(false);
            return DeviceSnapshots.Read<DeviceWakeState>(snapshot.Data);
        }, log);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool succeeded = false;
                await boundary.RunAsync(async () =>
                {
                    await scheduler.TickAsync(stoppingToken).ConfigureAwait(false);
                    succeeded = true;
                }, "HostPowerSchedule", Guid.NewGuid(), "tick", stoppingToken).ConfigureAwait(false);
                await Task.Delay(succeeded ? TimeSpan.FromSeconds(1)
                                           : TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 元の受付位置は残し、停止中に送れなかった予定を次回起動で遡りません。
        }
    }
}
