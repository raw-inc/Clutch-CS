using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Snapshots;

/// <summary>スナップショットとファイル補償の照会・明示復旧を管理要求へ接続します。</summary>
internal static class SnapshotManagement
{
    /// <summary>元の操作IDを維持する照会と明示復旧だけを登録します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime service)
    {
        registry.Read("snapshots.list", async (value, cancellation) =>
        {
            SnapshotListQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<SnapshotListQuery>(value);
            }
            return await SnapshotListing.ReadAsync(service, query, cancellation).ConfigureAwait(false);
        });
        registry.Read("snapshots.scope", async (value, cancellation) => await service.Snapshots.ExpandScopeAsync(ManagementRegistry.Body<SnapshotScopeRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("snapshots.prepare", async (value, cancellation) => await service.Snapshots.PrepareRestoreAsync(ManagementRegistry.Body<SnapshotRestoreRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("snapshots.resolve", async (value, cancellation) => await service.Snapshots.ResolveDraftAsync(ManagementRegistry.Body<SnapshotDraftResolution>(value), cancellation).ConfigureAwait(false));
        registry.Read("snapshots.locks", async (_, cancellation) => await service.Snapshots.GetEditLocksAsync(cancellation).ConfigureAwait(false));
        registry.Write("snapshots.capture", async (value, cancellation) =>
        {
            SnapshotCaptureRequest request = ManagementRegistry.Body<SnapshotCaptureRequest>(value);
            return await service.Snapshots.CaptureAsync(request, cancellation).ConfigureAwait(false);
        });
        registry.Write("snapshots.restore", async (value, cancellation) => await service.Snapshots.RestoreAsync(ManagementRegistry.Body<SnapshotRestoreRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("snapshots.recover", async (value, cancellation) => await service.Snapshots.RecoverAsync(ManagementRegistry.Body<SnapshotRestoreRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("snapshots.captures", async (value, cancellation) => new
        {
            operationId = ManagementRegistry.OperationId(value), result = await service.Snapshots.GetCaptureOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false),
        });
        registry.Read("snapshots.restores", async (value, cancellation) => await service.Snapshots.GetRestoreOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Read("files.operations", async (value, cancellation) => await service.FileTransactions.Transactions.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("files.recover", async (value, cancellation) => await service.FileTransactions.Transactions.RecoverAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("files.reservations.release", async (value, cancellation) => await service.FileTransactions.Transactions.ReleaseReservationAsync(
            ManagementRegistry.Body<Contracts.Files.Transactions.FileReservationReleaseRequest>(value), cancellation).ConfigureAwait(false));
    }
}
