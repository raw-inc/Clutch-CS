using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>登録プロセス、実診断と新規入力の明示停止を管理します。</summary>
internal static class ProcessManagement
{
    /// <summary>汎用ノードと同じ実プロセス・資源境界を使用します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime service)
    {
        registry.Read("processes.list", (_, cancellation) => ListAsync(service, cancellation));
        registry.Write("processes.register", async (value, cancellation) =>
        {
            ProcessRegistrationRequest request = ManagementRegistry.Body<ProcessRegistrationRequest>(value);
            return new { outcome = await service.Processes.RegisterAsync(request.Definition, request.ExpectedVersion, cancellation).ConfigureAwait(false) };
        });
        registry.Write("processes.resolve-launch", async (value, cancellation) =>
        {
            ResolveProcessLaunchRequest request = ManagementRegistry.Body<ResolveProcessLaunchRequest>(value);
            return await service.Processes.ResolveLaunchAsync(request.Id, request.ConfirmedIdentity, cancellation).ConfigureAwait(false);
        });
        registry.Write("nodes.execute", async (value, cancellation) =>
        {
            RemoteExecutionRequest request = ManagementRegistry.Body<RemoteExecutionRequest>(value);
            TracePolicy trace = service.Trace.GetPolicy(request.WorkflowId, new(request.Trace, request.TraceSelection));
            request = request with { Operational = service.Operational.Capture(), Trace = trace.Enabled, TraceSelection = trace.Selection };
            return await service.Cluster.ExecuteAsync(request, cancellation).ConfigureAwait(false);
        });
        registry.Read("resources.capture", async (_, cancellation) => await service.Resources.CaptureAsync(cancellation).ConfigureAwait(false));
        registry.ReadValue("resources.latest", value =>
        {
            ResourceSample? sample = service.Resources.Latest;
            if (sample is not null && value is not null && ManagementRegistry.Body<ResourceSampleQuery>(value).Summary)
            {
                return DashboardSampleHistory.Project(sample);
            }
            return sample;
        });
        registry.Read("resources.history", async (_, cancellation) => await service.Resources.HistoryAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>登録内容と実体の観測結果を別々に返します。</summary>
    /// <returns>このPeerに登録したプロセスの現在状態です。</returns>
    private static async Task<object?> ListAsync(ServiceRuntime service, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RegisteredProcess> entries = await service.Processes.ListAsync(cancellationToken).ConfigureAwait(false);
        List<object> result = [];
        foreach (RegisteredProcess entry in entries)
        {
            ProcessOperationResult observed = await service.Processes.ObserveAsync(entry.Definition.Id, cancellationToken).ConfigureAwait(false);
            List<ProcessScheduleStatus> schedules = [];
            await foreach (ProcessScheduleStatus status in service.Processes.ReadSchedulesAsync(entry.Definition, cancellationToken).ConfigureAwait(false))
            {
                schedules.Add(status);
            }
            result.Add(new { entry.Version, entry.Definition, observed, schedules });
        }
        return result;
    }

}
