using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>Peerの診断、参加状態、秘密情報の管理を既存サービスへ接続します。</summary>
internal static class CoreManagement
{
    /// <summary>Core自身の状態と明示した運用操作を登録します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime service, IWorkflowLog log)
    {
        registry.ReadValue("health", _ => Health(service));
        Infrastructure.Processes.HostPowerController power = new(service.Configuration.Cluster.HostId, service.Cluster.LocalInstanceId, () => service.Status);
        registry.Write("host.power", async (value, cancellation) => await power.ExecuteAsync(ManagementRegistry.Body<HostPowerRequest>(value), cancellation).ConfigureAwait(false));
        Infrastructure.Processes.WakeOnLanSender wake = new();
        registry.Write("host.wake", async (value, cancellation) => await wake.SendAsync(ManagementRegistry.Body<HostWakeRequest>(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("status", _ => Status(service));
        registry.ReadValue("dashboard.current", _ => Current(service));
        registry.ReadValue("catalog", _ => service.Registry.Descriptors);
        Runtime.Files.Browsing.FileBrowser files = new(service.Configuration.Files);
        registry.Read("files.browse", (value, cancellation) => Task.FromResult<object?>(files.Browse(ManagementRegistry.Body<Contracts.Files.Browsing.FileBrowseRequest>(value), cancellation)));
        registry.ReadValue("network", _ => NetworkInterface.GetAllNetworkInterfaces().Select(item => new
        {
            item.Id, item.Name, item.Description, status = item.OperationalStatus.ToString(),
            addresses = item.GetIPProperties().UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork).Select(address => address.Address.ToString()),
        }).ToArray());
        registry.Write("cluster.detach", async (_, cancellation) =>
        {
            await service.Cluster.DetachAsync(cancellation).ConfigureAwait(false);
            await service.Sources.ReconcileAsync(cancellation).ConfigureAwait(false);
            return new { status = service.Status };
        });
        registry.Write("cluster.rejoin", async (_, cancellation) =>
        {
            await service.Cluster.RejoinAsync(cancellation).ConfigureAwait(false);
            return new { status = service.Status };
        });
        registry.Read("operations.read", async (value, cancellation) => await service.Cluster.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.Read("secrets.list", async (_, cancellation) => await service.Secrets.ListNamesAsync(cancellation).ConfigureAwait(false));
        registry.Write("secrets.set", (value, cancellation) => SetSecretAsync(ManagementRegistry.Body<SecretChangeRequest>(value), service, log, cancellation));
    }

    /// <summary>通常のhealthと一括照会で、同じ実体・参加判定を使います。</summary>
    /// <returns>今回確認した稼働状態です。</returns>
    private static object Health(ServiceRuntime service)
    {
        EPeerStatus status = service.Status;
        return new
        {
            status, clusterId = service.Configuration.Cluster.Id, ready = status == EPeerStatus.Ready, participating = PeerStatusRules.CanParticipate(status),
            peerId = service.Cluster.LocalPeerId, instanceId = service.Cluster.LocalInstanceId,
            ServiceIdentity.Current.ProcessId, ServiceIdentity.Current.ProcessStartedAt, ServiceIdentity.Current.BuildVersion,
            service.StartupProblem, synchronizationProblem = service.Cluster.SynchronizationProblem ?? service.ReadinessProblem, historySynchronization = service.Cluster.HistorySynchronization, maintenance = service.Maintenance.State,
        };
    }

    /// <summary>通常のstatusと一括照会で、同じ診断項目を返します。</summary>
    /// <returns>値本文を含まない現在の状態です。</returns>
    private static object Status(ServiceRuntime service) => new
    {
        clusterId = service.Configuration.Cluster.Id, peerId = service.Cluster.LocalPeerId, instanceId = service.Cluster.LocalInstanceId,
        coordinator = service.Cluster.CoordinatorId, liveWorkflowUpdates = service.Operational.Capture()?.Settings.LiveWorkflowUpdates == true, status = service.Status, peers = service.Cluster.GetPeers(), groups = service.Cluster.GetHostGroups(),
        sources = service.Sources.GetSources(), runningNodes = service.Executor.Running, nodeValues = service.Executor.ValueObservations.Snapshot().Concat(service.Engine.ValueObservations.Snapshot()).ToArray(), sourceFailures = service.Sources.Failures,
        events = service.Events.Statistics,
        runtimeReceipts = service.Cluster.RuntimeStatistics,
        historySynchronization = service.Cluster.HistorySynchronization,
        executionHistoryProblem = service.ExecutionJournal.Problem,
        scheduleProblem = service.ScheduledInputs.LastProblem,
        schedules = new { service.ScheduledInputs.LocallyManagedInputs, service.ScheduledInputs.UnconfirmedResults },
        sourceQueue = service.Sources.Statistics, maintenance = service.Maintenance.State, service.StartupProblem, synchronizationProblem = service.Cluster.SynchronizationProblem ?? service.ReadinessProblem,
    };

    /// <summary>既存の収集済み状態を一往復にまとめ、新しい採取や保存は開始しません。</summary>
    /// <returns>Dashboardが使用する三つの読取り結果です。</returns>
    private static object Current(ServiceRuntime service)
    {
        ResourceSample? resource = service.Resources.Latest;
        if (resource is not null)
        {
            resource = DashboardSampleHistory.Project(resource);
        }
        return new { health = Health(service), status = Status(service), resource };
    }

    /// <summary>秘密の本文を応答・監査記録へ戻さず、保存結果だけを記録します。</summary>
    /// <returns>保存を確認した名前と結果です。</returns>
    private static async Task<object?> SetSecretAsync(SecretChangeRequest request, ServiceRuntime service, IWorkflowLog log, CancellationToken cancellationToken = default)
    {
        bool saved = false;
        try
        {
            await service.Secrets.SetAsync(request.Name, request.Value, cancellationToken).ConfigureAwait(false);
            saved = true;
        }
        finally
        {
            await ConfigurationAudit.WriteAsync(log, service.Cluster.LocalPeerId, "secrets.set", request.Name, request.Origin, saved, request.Value, CancellationToken.None).ConfigureAwait(false);
        }
        return new { request.Name, saved };
    }
}
