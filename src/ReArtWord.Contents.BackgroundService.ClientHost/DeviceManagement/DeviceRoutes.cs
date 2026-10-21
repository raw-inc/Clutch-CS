using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>HostManagerの画面に必要な固定対象・一括操作だけを公開します。</summary>
internal static class DeviceRoutes
{
    /// <summary>画面寿命と独立した受付と、読取りだけの結果照会を接続します。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/client/host-manager/hosts", async (DeviceRemote remote, HostPowerScheduleRemote schedules, CancellationToken cancellation) =>
            await schedules.IncludeOfflineAsync(await remote.HostsAsync(cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false));
        app.MapGet("/client/host-manager/power-schedules/clusters", (string hostId, HostPowerScheduleRemote remote, CancellationToken cancellation) => remote.ClustersAsync(hostId, cancellation));
        app.MapPost("/client/host-manager/power-schedules/read", (Contracts.DeviceManagement.PowerSchedules.HostPowerScheduleReadRequest request, HostPowerScheduleRemote remote, CancellationToken cancellation) => remote.ReadAsync(request, cancellation));
        app.MapPost("/client/host-manager/power-schedules/save", (Contracts.DeviceManagement.PowerSchedules.HostPowerScheduleSaveRequest request, HostPowerScheduleRemote remote, CancellationToken cancellation) => remote.SaveAsync(request, cancellation));
        app.MapPost("/client/host-manager/read", async (DeviceTargetRead request, DeviceRemote remote, WakeHostCache wake, CancellationToken cancellation) =>
        {
            long order = wake.BeginRead();
            DeviceSnapshot snapshot = await remote.ReadAsync(request, cancellation).ConfigureAwait(false);
            if (request.Query.Area == EDeviceArea.WakeOnLan)
            {
                await wake.ObserveAsync(request.Target, snapshot, order, cancellation).ConfigureAwait(false);
            }
            return snapshot;
        });
        app.MapPost("/client/host-manager/wake", (Contracts.Management.Overview.HostWakeStart request, WakeHostDispatch wake, CancellationToken cancellation) => wake.SendAsync(request, cancellation));
        app.MapGet("/client/host-manager/batches", (DeviceBatches batches, CancellationToken cancellation) => batches.ListAsync(cancellation));
        app.MapGet("/client/host-manager/unconfirmed-hosts", (DeviceBatches batches, CancellationToken cancellation) => batches.UnconfirmedHostsAsync(cancellation));
        app.MapPost("/client/host-manager/batches", (DeviceBatchRequest request, DeviceBatches batches, CancellationToken cancellation) => batches.EnqueueAsync(request, cancellation));
        app.MapGet("/client/host-manager/batches/{id:guid}", (Guid id, DeviceBatchStore store, CancellationToken cancellation) => store.ReadAsync(id, cancellation));
        app.MapPost("/client/host-manager/batches/{id:guid}/reconcile", (Guid id, DeviceBatches batches, CancellationToken cancellation) => batches.ReconcileAsync(id, cancellation));
    }
}
