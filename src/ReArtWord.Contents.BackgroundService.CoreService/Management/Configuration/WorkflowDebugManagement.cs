using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>保存済みWFの次回診断を、実行開始や通常traceの設定変更から分離して提供します。</summary>
internal static class WorkflowDebugManagement
{
    /// <summary>予約・取消と、有限な診断ページの参照を登録します。</summary>
    internal static void Register(ManagementRegistry registry, ServiceRuntime service)
    {
        registry.Write("workflow-debug.arm", async (value, cancellation) =>
        {
            WorkflowDebugRequest request = ManagementRegistry.Body<WorkflowDebugRequest>(value);
            if (await service.Workflows.GetAsync(request.WorkflowId, cancellation).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException();
            }
            return await service.Debug.ArmAsync(request, cancellation).ConfigureAwait(false);
        });
        registry.Read("workflow-debug.latest", async (value, cancellation) => await service.Debug.LatestAsync(ManagementRegistry.Id(value), cancellation).ConfigureAwait(false));
        registry.Read("workflow-debug.read", async (value, cancellation) => await service.Debug.ReadAsync(ManagementRegistry.Body<WorkflowDebugQuery>(value), cancellation).ConfigureAwait(false));
        registry.Write("workflow-debug.cancel", async (value, cancellation) => await service.Debug.CancelAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
    }
}
