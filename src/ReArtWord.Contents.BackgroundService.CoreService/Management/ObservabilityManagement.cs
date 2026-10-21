using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Observability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>ログの保持・検索と一時追跡の明示操作を登録します。</summary>
internal static class ObservabilityManagement
{
    /// <summary>診断の参照と状態を変更する操作を区別します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime, WorkflowLogService log, LogRetentionCoordinator retention)
    {
        TraceService trace = runtime.Trace;
        ClusterStopService stop = runtime.ClusterStop;
        registry.Read("logs.query", async (value, cancellation) => await log.QueryAsync(ManagementRegistry.Body<LogQuery>(value), cancellation).ConfigureAwait(false));
        registry.Read("logs.names", async (value, cancellation) => await NamesAsync(runtime, ManagementRegistry.Body<LogNamesQuery>(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("logs.statistics", _ => log.Statistics);
        registry.ReadValue("logs.options", _ => log.Options);
        registry.Read("logs.retention", async (_, cancellation) => new LogRetentionView(await runtime.Operational.GetAsync(cancellation).ConfigureAwait(false), retention.Status));
        registry.Write("logs.change-options", async (value, cancellation) => await LogRetentionManagement.ChangeAsync(ManagementRegistry.Body<LogOptionsChangeRequest>(value), runtime, log, retention, cancellation).ConfigureAwait(false));
        registry.Read("trace.read", (_, cancellation) => ReadTraceAsync(runtime, cancellation));
        registry.Write("trace.change", async (value, cancellation) =>
        {
            TraceChange request = ManagementRegistry.Body<TraceChange>(value);
            if (await runtime.Workflows.GetAsync(request.WorkflowId, cancellation).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException();
            }
            Infrastructure.Persistence.Receipts.DurableReceipt receipt = await trace.ChangeAsync(request, cancellation).ConfigureAwait(false);
            return new { request.OperationId, receipt.Outcome, receipt.Reason, receipt.Entries };
        });
        registry.Read("trace.operations", async (value, cancellation) => await trace.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("trace.cluster-stop", async (value, cancellation) => await stop.RequestAsync(ManagementRegistry.Body<TraceClusterStopRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("trace.cluster-stop-operation", async (value, cancellation) => await stop.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("trace.release-stop", async (value, cancellation) =>
        {
            TraceStopReleaseRequest request = ManagementRegistry.Body<TraceStopReleaseRequest>(value);
            Infrastructure.Persistence.Receipts.DurableReceipt receipt = await trace.ReleaseStopAsync(request, cancellation).ConfigureAwait(false);
            return new { request.OperationId, receipt.Outcome, receipt.Reason, message = "一時追跡の再開始保留を解除する操作です。受付済みのHost停止要求は取り消しません。" };
        });
    }

    /// <summary>保存定義の論理名だけを返し、大きいスクリプトや接続情報をログ出力へ複製しません。</summary>
    /// <returns>指定ノードの名前と参照した保存版です。</returns>
    private static async Task<LogNames> NamesAsync(ServiceRuntime runtime, LogNamesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkflowId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.WorkflowId.Length, 256);
        ArgumentNullException.ThrowIfNull(query.NodeIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.NodeIds.Count, 256);
        foreach (string id in query.NodeIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(id.Length, 256);
        }
        StoredWorkflow? workflow = await runtime.Workflows.GetAsync(query.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (workflow is null)
        {
            return new(null, null, new Dictionary<string, string>(StringComparer.Ordinal));
        }
        HashSet<string> wanted = new(query.NodeIds, StringComparer.Ordinal);
        Dictionary<string, string> names = workflow.Definition.Nodes.Where(node => wanted.Contains(node.Id) && !string.IsNullOrWhiteSpace(node.LogicalName))
            .ToDictionary(node => node.Id, node => node.LogicalName!, StringComparer.Ordinal);
        return new(workflow.Definition.Name, workflow.Revision, names);
    }

    /// <summary>保存候補、実稼働版の通常方針、一時追跡を区別して返します。</summary>
    /// <returns>ワークフロー別の追跡状態と停止保留です。</returns>
    private static async Task<object?> ReadTraceAsync(ServiceRuntime runtime, CancellationToken cancellationToken = default)
    {
        TraceService trace = runtime.Trace;
        await trace.RefreshAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredWorkflow> workflows = await runtime.Workflows.ListAsync(cancellationToken).ConfigureAwait(false);
        TraceStatus[] states = workflows.Select(workflow =>
        {
            WorkflowDefinition saved = workflow.Definition;
            WorkflowDefinition? active = runtime.Sources.GetEffective(saved.Id)?.Definition;
            TracePolicy normal = new(false);
            if (active is not null)
            {
                normal = new(active.Trace, active.TraceSelection);
            }
            return trace.GetStatus(saved.Id, new TracePolicy(saved.Trace, saved.TraceSelection), normal);
        }).ToArray();
        return new
        {
            trace.Loaded, trace.Version, states, closing = await trace.GetStopStateAsync(cancellationToken).ConfigureAwait(false),
            targets = runtime.Cluster.GetPeers().Select(peer => new TraceStopTarget(peer.PeerId, peer.InstanceId)).ToArray(),
        };
    }
}
