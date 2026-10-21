using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>HostManagerのOS読取りと永続変更をCoreの固定実体へ接続します。</summary>
internal static class DeviceManagement
{
    /// <summary>CoreのDIコンテナーが寿命を所有する設定サービスを構成します。</summary>
    /// <returns>コンテナーの終了まで保持する設定サービスです。</returns>
    internal static DeviceManagementService Create(ServiceRuntime runtime)
    {
        return new(runtime.Configuration.Cluster.HostId, runtime.Cluster.LocalInstanceId,
            runtime.Cluster.LocalPeerId, runtime.Repository, runtime.Configuration.DataDirectory, OperatingSystem.IsWindows() ? new CoreService.DeviceManagement.DisplayAccess()
                                                                                                                              : null);
    }

    /// <summary>登録処理の終了後も、同じサービスで開始・読取り・結果照会を処理します。</summary>
    internal static void Register(ManagementRegistry management, DeviceManagementService service)
    {
        management.ReadValue("host-manager.capabilities", _ => service.Capabilities());
        management.Read("host-manager.read", async (body, cancellation) =>
            await service.ReadAsync(ManagementRegistry.Body<DeviceReadRequest>(body), cancellation).ConfigureAwait(false));
        management.Write("host-manager.apply", async (body, cancellation) =>
            await service.ApplyAsync(ManagementRegistry.Body<DeviceChangeRequest>(body), cancellation).ConfigureAwait(false));
        management.Read("host-manager.operations", async (body, cancellation) =>
            await service.ReadOperationAsync(ManagementRegistry.OperationId(body), cancellation).ConfigureAwait(false));
    }
}
