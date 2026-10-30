using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>定義の保存・反映と実行の受付・結果照会を分けて公開します。</summary>
internal static class WorkflowManagement
{
    /// <summary>既存の保存と実行サービスを管理要求へ接続します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime service, WorkflowRunService runs)
    {
        WorkflowDebugManagement.Register(registry, service);
        registry.Read("workflows.concurrency", async (value, cancellation) => await service.Concurrency.ReadAsync(
            ManagementRegistry.Body<Contracts.Execution.Concurrency.WorkflowConcurrencyTarget[]>(value), cancellation).ConfigureAwait(false));
        WfDashboardCatalog dashboardCatalog = new(service.Workflows, service.Registry);
        WfDashboardValues dashboardValues = new(service.Workflows, service.Registry);
        WfDashboardStates dashboardStates = new(service, dashboardValues);
        registry.Read("dashboards.catalog", async (value, cancellation) => await dashboardCatalog.ReadAsync(ManagementRegistry.Body<Contracts.Workflow.Dashboards.WfDashboardCatalogQuery>(value), cancellation).ConfigureAwait(false));
        registry.Read("dashboards.nodes", async (value, cancellation) => await dashboardStates.ReadAsync(ManagementRegistry.Body<Contracts.Workflow.Dashboards.WfDashboardNodeQuery>(value), cancellation).ConfigureAwait(false));
        registry.Read("dashboards.value-prepare", async (value, cancellation) => await dashboardValues.PrepareAsync(ManagementRegistry.Body<Contracts.Workflow.Dashboards.WfDashboardValueRequest>(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("workflows.activity", value =>
        {
            string id = ManagementRegistry.Id(value);
            IEnumerable<NodeValueObservation> sources = service.Sources.GetSources().Where(source => string.Equals(source.WorkflowId, id, StringComparison.Ordinal)
                && source.Result is null && source.Status.State is ESourceState.Starting or ESourceState.Listening)
                .Select(source => new NodeValueObservation(source.Id, source.Id, id, source.Revision, source.NodeId, source.PeerId, source.StartedAt));
            NodeValueObservation[] active = service.Executor.ValueObservations.Snapshot(id, runningOnly: true).Concat(service.Engine.ValueObservations.Snapshot(id, runningOnly: true))
                .Concat(sources).DistinctBy(item => (item.NodeId, item.Revision)).Take(1024).ToArray();
            return new WorkflowNodeActivity(id, service.Cluster.LocalPeerId, service.Cluster.LocalInstanceId, active);
        });
        registry.ReadValue("previews.list", value => service.Previews.Read(ManagementRegistry.Body<NodePreviewQuery>(value)));
        registry.ReadValue("previews.history", value => service.Previews.ReadHistory(ManagementRegistry.Body<NodePreviewHistoryQuery>(value)));
        registry.ReadValue("previews.chunk", value => service.Previews.ReadChunk(ManagementRegistry.Body<NodePreviewChunkRequest>(value)));
        registry.Read("workflows.recipe-preview", async (value, cancellation) => await WorkflowRecipeInspection.ReadAsync(service, ManagementRegistry.Body<WorkflowRecipePreviewQuery>(value), cancellation).ConfigureAwait(false));
        WorkflowCompatibilityInspection compatibility = new(service.Workflows, service.SnapshotCatalog, service.Compiler, service.Registry, service.Sources.GetEffective, service.Cluster.LocalPeerId);
        registry.Read("scripts.analyze", async (value, cancellation) =>
        {
            Contracts.Scripting.ScriptAnalysisRequest request = ManagementRegistry.Body<Contracts.Scripting.ScriptAnalysisRequest>(value);
            return await service.Scripts.AnalyzeAsync(request, cancellation).ConfigureAwait(false);
        });
        registry.ReadValue("scripts.packages.list", _ => service.Scripts.Management.Catalog());
        registry.Read("scripts.runtimes.list", async (value, cancellation) => await service.Scripts.Runtimes.CatalogAsync(ManagementRegistry.Body<Contracts.Scripting.Runtimes.ScriptRuntimeQuery>(value), cancellation).ConfigureAwait(false));
        registry.Write("scripts.runtimes.change", async (value, cancellation) => await service.Scripts.Runtimes.ChangeAsync(ManagementRegistry.Body<Contracts.Scripting.Runtimes.ScriptRuntimeChange>(value), cancellation).ConfigureAwait(false));
        registry.Read("scripts.runtimes.releases", async (value, cancellation) => await service.Scripts.Installer.ReleasesAsync(ManagementRegistry.Body<Contracts.Scripting.Runtimes.ScriptRuntimeQuery>(value).Language, cancellation).ConfigureAwait(false));
        registry.ReadValue("scripts.runtimes.installations", _ => service.Scripts.Installer.Status());
        registry.ReadValue("scripts.runtimes.operation", value => service.Scripts.Installer.Operation(ManagementRegistry.OperationId(value)));
        registry.Write("scripts.runtimes.install", (value, _) => Task.FromResult<object?>(service.Scripts.Installer.Start(ManagementRegistry.Body<Contracts.Scripting.Runtimes.ScriptRuntimeInstallRequest>(value))));
        registry.ReadValue("scripts.packages.operation", value => service.Scripts.Packages.Operation(ManagementRegistry.OperationId(value)));
        registry.Write("scripts.packages.change", (value, _) => Task.FromResult<object?>(service.Scripts.Management.Change(ManagementRegistry.Body<Contracts.Scripting.Packages.ScriptPackageChangeRequest>(value))));
        registry.Read("workflows.compatibility", async (value, cancellation) => await compatibility.InspectAsync(ManagementRegistry.Body<WorkflowCompatibilityQuery>(value), cancellation).ConfigureAwait(false));
        registry.Read("workflows.list", async (value, cancellation) =>
        {
            WorkflowListQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<WorkflowListQuery>(value);
            }
            return await WorkflowListing.ReadAsync(service, query, cancellation).ConfigureAwait(false);
        });
        registry.Read("workflows.read", async (value, cancellation) => WorkflowListing.View(await service.Workflows.GetAsync(ManagementRegistry.Id(value), cancellation).ConfigureAwait(false) ?? throw new KeyNotFoundException(), service));
        registry.Read("workflows.definition-chunk", async (value, cancellation) => await WorkflowListing.ChunkAsync(service, ManagementRegistry.Body<WorkflowDefinitionChunkRequest>(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("workflows.validate", value =>
        {
            Runtime.Workflows.Compilation.CompilationResult result = service.Compiler.Compile(ManagementRegistry.Body<WorkflowDefinition>(value));
            return new { valid = result.Workflow is not null, result.Issues, revision = result.Workflow?.Revision };
        });
        registry.ReadValue("workflows.parse", value => WorkflowYamlReader.Read(ManagementRegistry.Body<WorkflowYamlImport>(value).Text));
        registry.ReadValue("workflows.export-yaml", value => WorkflowYamlWriter.Write(ManagementRegistry.Body<WorkflowDefinition>(value)));
        registry.Write("workflows.save", async (value, cancellation) => await service.Workflows.SaveAsync(ManagementRegistry.Body<WorkflowSaveRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("workflows.delete", async (value, cancellation) => await service.Workflows.DeleteAsync(ManagementRegistry.Body<WorkflowDeleteRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("workflows.deletions", async (value, cancellation) => await service.Workflows.GetDeleteOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("workflows.merge", async (value, cancellation) => await service.Workflows.MergeAsync(ManagementRegistry.Body<WorkflowMergeRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("workflows.activate", async (value, cancellation) => await service.Workflows.ActivateAsync(ManagementRegistry.Body<ActivationRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("workflows.impact", async (value, cancellation) => await service.Workflows.InspectImpactAsync(ManagementRegistry.Id(value), cancellation).ConfigureAwait(false));
        registry.Read("workflows.operations", async (value, cancellation) => await service.Workflows.GetSaveOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Write("runs.start", async (value, cancellation) => await runs.StartAsync(ManagementRegistry.Body<WorkflowRunRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("runs.read", async (value, cancellation) => await runs.ReadAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false) ?? throw new KeyNotFoundException());
        registry.Read("runs.active", async (value, cancellation) =>
        {
            ActiveRunsQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<ActiveRunsQuery>(value);
            }
            return await runs.ActiveAsync(query, cancellation).ConfigureAwait(false);
        });
        registry.Write("runs.cancel", async (value, cancellation) => await runs.Cancellations.CancelAsync(ManagementRegistry.Body<WorkflowCancelRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("runs.cancellation", async (value, cancellation) => await runs.Cancellations.ReadAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false) ?? throw new KeyNotFoundException());
        registry.ReadValue("executions.list", value =>
        {
            ExecutionHistoryQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<ExecutionHistoryQuery>(value);
            }
            return ExecutionHistory.Page(service.Engine.RecentViews().Concat(service.ExecutionJournal.Recent()).DistinctBy(WorkflowExecutionJournal.Identity), query);
        });
        registry.ReadValue("executions.activity", value => ExecutionHistory.ActivityPage(
            service.Engine.RecentViews().Concat(service.ExecutionJournal.Recent()).DistinctBy(WorkflowExecutionJournal.Identity),
            value is null ? new ExecutionHistoryQuery()
                          : ManagementRegistry.Body<ExecutionHistoryQuery>(value)));
        registry.Write("executions.cancel", async (value, cancellation) => new
        {
            requested = await service.Executor.RequestCancellationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false),
            message = "取消要求の受付です。実停止は元の実行結果で確認してください。",
        });
        registry.Write("sources.stop", async (value, cancellation) =>
        {
            SourceStopRequest request = ManagementRegistry.Body<SourceStopRequest>(value);
            if (request.SourceId is not Guid source || source == Guid.Empty || request.PeerInstanceId != service.Cluster.LocalInstanceId)
            {
                return NodeResult.Failure("source.identity", "状態一覧で確認した待受IDとPeer実体を指定してください。", EOutcome.NotExecuted);
            }
            return await service.Sources.StopAsync(request.WorkflowId, request.NodeId, request.Cancel, source, cancellationToken: cancellation).ConfigureAwait(false);
        });
        registry.Read("configuration.read", async (_, cancellation) => await service.Operational.GetAsync(cancellation).ConfigureAwait(false));
        registry.Read("configuration.impact", async (_, cancellation) => await service.Operational.InspectImpactAsync(cancellation).ConfigureAwait(false));
        registry.Write("configuration.save", async (value, cancellation) => await service.Operational.SaveAsync(ManagementRegistry.Body<ClusterOperationalSaveRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("configuration.activate", async (value, cancellation) => await service.Operational.ActivateAsync(ManagementRegistry.Body<ClusterOperationalActivateRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("configuration.operations", async (value, cancellation) => await service.Operational.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
    }

}
