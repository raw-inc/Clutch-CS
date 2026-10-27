namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>GUI・CLIのローカル経路とCoreの管理操作名の対応です。</summary>
internal static class DomainRoutes
{
    /// <summary>HTTPはローカル画面の境界に限定し、CoreのHTTP待受を使用しません。</summary>
    public static void Map(WebApplication app)
    {
        ManagementFacade.MapRoute(app, "POST", "/api/archive/export", "archive.export", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/archive/upload/begin", "archive.upload.begin", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/archive/upload/chunk", "archive.upload.chunk", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/archive/preview", "archive.preview", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/archive/import", "archive.import", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/archive/operations/{id:guid}", "archive.operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/archive/read", "archive.read", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/archive/operations/{id:guid}/reconcile", "archive.reconcile", id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/previews/list", "previews.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/previews/history", "previews.history", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/previews/chunk", "previews.chunk", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/concurrency/status", "workflows.concurrency", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/recipe-preview", "workflows.recipe-preview", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/health", "health", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/host/power", "host.power", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/analyze", "scripts.analyze", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/runtimes/list", "scripts.runtimes.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/runtimes/change", "scripts.runtimes.change", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/runtimes/releases", "scripts.runtimes.releases", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/scripts/runtimes/installations", "scripts.runtimes.installations", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/scripts/runtimes/operations/{id:guid}", "scripts.runtimes.operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/runtimes/install", "scripts.runtimes.install", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/scripts/packages", "scripts.packages.list", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/scripts/packages/operations/{id:guid}", "scripts.packages.operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/scripts/packages/change", "scripts.packages.change", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/updates/schedules", "updates.schedules.list", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/updates/schedules/status", "updates.schedules.status", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/updates/schedules/{id:guid}", "updates.schedules.read", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/updates/schedules/operations/{id:guid}", "updates.schedules.operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/updates/schedules/save", "updates.schedules.save", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/updates/schedules/cancel", "updates.schedules.cancel", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/terminal/sessions", "terminal.list", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/terminal/shells", "terminal.shells", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/terminal/capabilities", "terminal.capabilities", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/terminal/settings/save", "terminal.settings.save", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/terminal/read", "terminal.read", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/terminal/input", "terminal.input", body: true, transient: true);
        foreach (string action in new[] { "create", "claim", "close" })
        {
            ManagementFacade.MapRoute(app, "POST", "/api/terminal/" + action, "terminal." + action, body: true);
        }
        foreach (string name in new[] { "status", "catalog", "network" })
        {
            ManagementFacade.MapRoute(app, "GET", "/api/" + name, name, readOnly: true);
        }
        ManagementFacade.MapRoute(app, "POST", "/api/cluster/detach", "cluster.detach");
        ManagementFacade.MapRoute(app, "POST", "/api/cluster/rejoin", "cluster.rejoin");
        ManagementFacade.MapRoute(app, "GET", "/api/operations/{id:guid}", "operations.read", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/secrets", "secrets.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/files/browse", "files.browse", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/browser-files/open", "transfer.open", body: true, transient: true);
        ManagementFacade.MapRoute(app, "POST", "/api/browser-files/directory", "transfer.directory", body: true, transient: true);
        ManagementFacade.MapRoute(app, "POST", "/api/browser-files/discard", "transfer.discard", body: true, transient: true);
        ManagementFacade.MapRoute(app, "POST", "/api/browser-files/commit", "transfer.commit", body: true);
        ManagementFacade.MapRoute(app, "PUT", "/api/secrets", "secrets.set", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/conflicts", "conflicts.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/conflicts/query", "conflicts.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/conflicts/detail", "conflicts.detail", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/conflicts/resolve", "conflicts.resolve", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/conflicts/operations/{id:guid}", "conflicts.operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/availability", "availability.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/availability/query", "availability.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/internal-data/read", "internal-data.read", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/internal-data/write", "internal-data.write", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/internal-data/compare-exchange", "internal-data.compare-exchange", body: true);
        MapWorkflows(app);
        MapOperations(app);
        MapObservability(app);
    }

    /// <summary>保存・反映と未保存共同編集を別の管理経路へ送ります。</summary>
    private static void MapWorkflows(WebApplication app)
    {
        ManagementFacade.MapRoute(app, "GET", "/api/workflows", "workflows.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/dashboards/catalog", "dashboards.catalog", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/dashboards/nodes", "dashboards.nodes", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/dashboards/value-prepare", "dashboards.value-prepare", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/compatibility", "workflows.compatibility", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/query", "workflows.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/definition-chunk", "workflows.definition-chunk", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/detached", "workflows.detached", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/detached/query", "workflows.detached", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/{id}/detached", "workflows.detached-detail", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/workflows/detached/resolve", "workflows.detached-resolve", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/detached/operations/{id:guid}", "workflows.detached-operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/{id}", "workflows.read", readOnly: true, id: "id");
        foreach (string name in new[] { "validate", "parse", "export-yaml" })
        {
            ManagementFacade.MapRoute(app, "POST", "/api/workflows/" + name, "workflows." + name, readOnly: true, body: true);
        }
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/{id}/impact", "workflows.impact", readOnly: true, id: "id");
        foreach (string name in new[] { "save", "merge", "activate", "delete" })
        {
            ManagementFacade.MapRoute(app, "POST", "/api/workflows/" + name, "workflows." + name, body: true);
        }
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/operations/{id:guid}", "workflows.operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/deletions/{id:guid}", "workflows.deletions", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/configuration", "configuration.read", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/configuration/impact", "configuration.impact", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/configuration/save", "configuration.save", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/configuration/activate", "configuration.activate", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/configuration/operations/{id:guid}", "configuration.operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/{workflowId}/activity", "workflows.activity", readOnly: true, id: "workflowId");
        ManagementFacade.MapRoute(app, "GET", "/api/workflows/{workflowId}/debug", "workflow-debug.latest", readOnly: true, id: "workflowId");
        ManagementFacade.MapRoute(app, "POST", "/api/workflow-debug/arm", "workflow-debug.arm", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflow-debug/read", "workflow-debug.read", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/workflow-debug/{id:guid}/cancel", "workflow-debug.cancel", id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/collaboration/{workflowId}/apply-result", "collaboration.apply-result", readOnly: true, id: "workflowId");
        ManagementFacade.MapRoute(app, "GET", "/api/collaboration/{workflowId}/draft", "collaboration.draft", readOnly: true, id: "workflowId");
        ManagementFacade.MapRoute(app, "GET", "/api/collaboration/{workflowId}/presence", "collaboration.presence", readOnly: true, id: "workflowId");
        ManagementFacade.MapRoute(app, "POST", "/api/collaboration/{workflowId}/changes", "collaboration.changes", body: true, transient: true);
        ManagementFacade.MapRoute(app, "POST", "/api/collaboration/{workflowId}/presence", "collaboration.update-presence", body: true, transient: true);
    }

    /// <summary>実行・プロセス・スナップショットを元操作IDで参照します。</summary>
    private static void MapOperations(WebApplication app)
    {
        ManagementFacade.MapRoute(app, "POST", "/api/runs", "runs.start", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/runs/{id:guid}", "runs.read", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/runs/active", "runs.active", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/runs/active/query", "runs.active", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/runs/cancel", "runs.cancel", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/runs/cancellations/{id:guid}", "runs.cancellation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "GET", "/api/executions", "executions.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/executions/query", "executions.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/executions/{id:guid}/cancel", "executions.cancel", id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/sources/stop", "sources.stop", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/processes", "processes.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/processes/register", "processes.register", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/processes/remove", "processes.remove", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/processes/resolve-launch", "processes.resolve-launch", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/nodes/execute", "nodes.execute", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/resources", "resources.capture", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/resources/latest", "resources.latest", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/resources/history", "resources.history", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/safety", "safety.read", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/safety/operations/{id:guid}", "safety.operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/safety", "safety.change", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/safety/preview", "safety.preview", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/snapshots", "snapshots.list", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/snapshots/query", "snapshots.list", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/snapshots/scope", "snapshots.scope", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/snapshots/prepare", "snapshots.prepare", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/snapshots/resolve", "snapshots.resolve", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/snapshots/locks", "snapshots.locks", readOnly: true);
        foreach (string name in new[] { "capture", "restore", "recover" })
        {
            ManagementFacade.MapRoute(app, "POST", "/api/snapshots/" + name, "snapshots." + name, body: true);
        }
        foreach (string name in new[] { "captures", "restores" })
        {
            ManagementFacade.MapRoute(app, "GET", "/api/snapshots/" + name + "/{id:guid}", "snapshots." + name, readOnly: true, id: "id");
        }
        ManagementFacade.MapRoute(app, "GET", "/api/files/operations/{id:guid}", "files.operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/files/operations/{id:guid}/recover", "files.recover", id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/files/reservations/release", "files.reservations.release", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/updates/start", "updates.start", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/updates/{id:guid}", "updates.read", readOnly: true, id: "id");
    }

    /// <summary>ログ参照と追跡・保持設定変更の副作用を区別します。</summary>
    private static void MapObservability(WebApplication app)
    {
        ManagementFacade.MapRoute(app, "POST", "/api/logs/query", "logs.query", readOnly: true, body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/logs/statistics", "logs.statistics", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/logs/options", "logs.options", readOnly: true);
        ManagementFacade.MapRoute(app, "PUT", "/api/logs/options", "logs.change-options", body: true);
        ManagementFacade.MapRoute(app, "POST", "/api/logs/retention", "logs.change-options", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/logs/retention", "logs.retention", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/history/status", "history.archive.status", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/history/checkpoints", "history.checkpoints", readOnly: true);
        ManagementFacade.MapRoute(app, "GET", "/api/trace", "trace.read", readOnly: true);
        ManagementFacade.MapRoute(app, "POST", "/api/trace", "trace.change", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/trace/operations/{id:guid}", "trace.operations", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/trace/cluster-stop", "trace.cluster-stop", body: true);
        ManagementFacade.MapRoute(app, "GET", "/api/trace/cluster-stop/{id:guid}", "trace.cluster-stop-operation", readOnly: true, id: "id");
        ManagementFacade.MapRoute(app, "POST", "/api/trace/cluster-stop/release", "trace.release-stop", body: true);
    }
}
