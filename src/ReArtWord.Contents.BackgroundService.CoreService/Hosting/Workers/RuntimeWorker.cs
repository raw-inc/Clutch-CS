using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Workers;

/// <summary>Peer参加、実稼働版の反映と独立診断収集をHostの寿命へ接続します。</summary>
internal sealed class RuntimeWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ServiceRuntime runtime;
    private readonly ServiceBootstrap bootstrap;
    private readonly IHostApplicationLifetime host;
    private readonly IHostLifetime service;
    private readonly WorkflowLogService log;
    private readonly LogRetentionCoordinator retention;
    private readonly ResourceSelectionExchange resourceSelections;
    private readonly SourcePcAssignments sourcePcs;
    private readonly WorkflowEventExchange eventExchange;
    private readonly PeerEventObservations peerEvents;
    private static string ReconciliationFailure => "設定または稼働反映の確認に失敗しました。保存状態、Peer状態と操作結果を確認してください。";

    /// <summary>共通サービスと実待受開始の通知を指定します。</summary>
    public RuntimeWorker(ServiceRuntime runtime, ServiceBootstrap bootstrap, IHostApplicationLifetime host, IHostLifetime service, WorkflowLogService log, LogRetentionCoordinator retention)
    {
        this.runtime = runtime;
        this.bootstrap = bootstrap;
        this.host = host;
        this.service = service;
        this.log = log;
        this.retention = retention;
        log.Events = runtime.Events;
        eventExchange = new(runtime.Cluster, runtime.Events);
        peerEvents = new(runtime.Cluster, runtime.Events);
        resourceSelections = new(runtime.Cluster, () => runtime.Resources.Latest);
        runtime.Engine.Targets.ResourceObservations = resourceSelections.Capture;
        sourcePcs = new(runtime.Cluster, runtime.Repository, runtime.Sources, runtime.Registry, resourceSelections.Capture);
        runtime.Engine.Targets.SourceTargets = sourcePcs.Read;
        runtime.ClusterStop.StopHost = host.StopApplication;
    }

    /// <summary>同期と設定適用を定期確認し、開始済み処理を追跡したまま運用します。</summary>
    /// <returns>Host終了までの実作業です。</returns>
    // 抑制理由: Hostの所有境界で障害を終了状態へ変換し、入力や秘密を含む例外本文は公開しません。
    [SuppressMessage("Design", "CA1031", Justification = "背景作業の起動失敗を正常終了とせず、Host終了codeと一般診断へ反映します。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCoreAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 明示された通常停止です。
        }
        catch (Exception)
        {
            runtime.StartupFailed = true;
            runtime.StartupProblem = "Peerの起動に必要な保存状態・所有identity・設定反映を確認できません。";
            try
            {
                await Console.Error.WriteLineAsync(runtime.StartupProblem).ConfigureAwait(false);
            }
            finally
            {
                ServiceHostFailure.Stop(service, host);
            }
        }
    }

    /// <summary>診断と通常運用の所有タスクを終了まで追跡します。</summary>
    /// <returns>Host寿命に従う実作業です。</returns>
    // 抑制理由: 診断収集は監視ループと並行し、finallyで必ず終了をawaitします。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "Taskはメソッド末尾でawaitして所有する作業の終了を確認します。")]
    private async Task RunCoreAsync(CancellationToken stoppingToken = default)
    {
        await runtime.Maintenance.LoadAsync(stoppingToken).ConfigureAwait(false);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = host.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        runtime.ManagementStarted = true;
        await runtime.Cluster.StartAsync(stoppingToken).ConfigureAwait(false);
        using CancellationTokenSource workers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task resources = runtime.Resources.RunAsync(workers.Token);
        Task updates = runtime.Updater?.Stream.RunAsync(workers.Token) ?? Task.CompletedTask;
        Task schedules = runtime.Updater?.Scheduler.RunAsync(workers.Token) ?? Task.CompletedTask;
        Task events = eventExchange.RunAsync(workers.Token);
        Task network = Runtime.Networking.NetworkInterfaceObservation.RunAsync(workers.Token);
        Task history = runtime.ExecutionJournal.RunAsync(workers.Token);
        ProcessAutomation applicationAutomation = new(runtime.Processes, runtime.Cluster.LocalPeerId, log);
        Task applications = applicationAutomation.RunAsync(() => runtime.ManagementStarted && !runtime.Maintenance.IsClosed && PeerStatusRules.CanParticipate(runtime.Status), workers.Token);
        try
        {
            await runtime.ManualSafety.RefreshAsync(stoppingToken).ConfigureAwait(false);
            await runtime.Trace.RefreshAsync(stoppingToken).ConfigureAwait(false);
            await runtime.Readiness.InitializeOperationalAsync(false, stoppingToken).ConfigureAwait(false);
            await runtime.Workflows.RestoreEffectiveAsync(stoppingToken).ConfigureAwait(false);
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.ManagementStarted = false;
            await workers.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(resources, updates, schedules, events, network, history, applications).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workers.IsCancellationRequested)
            {
                // 診断収集の実停止を確認しました。
            }
        }
    }

    /// <summary>実共有版、役割変更と自動開始を確認します。</summary>
    /// <returns>終了要求までの監視です。</returns>
    // 抑制理由: 設定一件の失敗を管理画面へ残し、Peer全体を停止させません。
    [SuppressMessage("Design", "CA1031", Justification = "初期導入と反映の障害を可視化して、APIからの修復を維持します。")]
    private async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        bool initialized = false;
        EPeerStatus previous = runtime.Status;
        string? previousForward = null;
        bool traceWasAvailable = true;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));
        do
        {
            try
            {
                await resourceSelections.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await peerEvents.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await runtime.Conflicts.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                await runtime.ManualSafety.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await runtime.Trace.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (!runtime.Trace.Loaded && traceWasAvailable)
                {
                    await WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", runtime.Cluster.LocalPeerId,
                        "一時追跡の現在状態を確認できません。通常処理と開始済み方針は継続し、追跡変更は確認できるまで保留します。"), cancellationToken).ConfigureAwait(false);
                }
                traceWasAvailable = runtime.Trace.Loaded;
                await runtime.ClusterStop.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (runtime.Updater is { } updater)
                {
                    await updater.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                int? logPort = null;
                if (log.Statistics.ListenEndpoint is string address && IPEndPoint.TryParse(address, out IPEndPoint? endpoint))
                {
                    logPort = endpoint.Port;
                }
                runtime.Cluster.SetLocalLogPort(logPort);
                PeerInfo? coordinator = runtime.Cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, runtime.Cluster.CoordinatorId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status) && peer.LogPort is not null);
                string? target = coordinator?.InstanceId.ToString("D") + ":" + coordinator?.LogPort;
                if (!string.Equals(previousForward, target, StringComparison.Ordinal))
                {
                    IReadOnlyList<LogForwardTarget> targets = [];
                    if (coordinator is not null && !string.Equals(coordinator.PeerId, runtime.Cluster.LocalPeerId, StringComparison.Ordinal))
                    {
                        targets = [new LogForwardTarget(coordinator.PeerId, coordinator.Address, coordinator.LogPort!.Value)];
                    }
                    await log.UpdateForwardTargetsAsync(targets, cancellationToken).ConfigureAwait(false);
                    previousForward = target;
                }
                if (PeerStatusRules.CanParticipate(runtime.Status) && !runtime.Maintenance.IsClosed)
                {
                    ConfigurationPhaseResult operational = await runtime.Readiness.InitializeOperationalAsync(runtime.Configuration.Cluster.NewCluster, cancellationToken).ConfigureAwait(false);
                    if (!initialized && operational.Outcome == Contracts.Execution.EOutcome.Succeeded)
                    {
                        initialized = true;
                        await bootstrap.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await runtime.Workflows.ReconcileActivationAsync(cancellationToken).ConfigureAwait(false);
                    await runtime.Workflows.ReconcileDetachedAsync(cancellationToken).ConfigureAwait(false);
                    await runtime.State.ReconcileDetachedAsync(cancellationToken).ConfigureAwait(false);
                    await runtime.Persistence.AvailableState.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
                }
                ClusterOperationalSnapshot? currentSettings = runtime.Operational.Capture();
                await retention.ReconcileAsync(currentSettings, cancellationToken).ConfigureAwait(false);
                if (currentSettings is not null)
                {
                    runtime.Cluster.UpdateHostGroups(currentSettings.Settings.HostGroups);
                }
                await sourcePcs.RefreshAsync(cancellationToken).ConfigureAwait(false);
                await runtime.Sources.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                if (!runtime.Maintenance.IsClosed && PeerStatusRules.CanParticipate(runtime.Status) && runtime.Updater is { } updateObserver)
                {
                    await updateObserver.Events.DrainAsync(cancellationToken).ConfigureAwait(false);
                }
                await runtime.ScheduledInputs.RefreshAsync(cancellationToken).ConfigureAwait(false);
                EPeerStatus current = runtime.Status;
                if (current != previous)
                {
                    await runtime.Events.PublishAsync(new WorkflowEvent(Guid.NewGuid(), "peer.status", DateTimeOffset.UtcNow, runtime.Cluster.LocalPeerId,
                        NodeValue.FromObject(new { previous, current })), cancellationToken).ConfigureAwait(false);
                    previous = current;
                }
                if (string.Equals(runtime.StartupProblem, ReconciliationFailure, StringComparison.Ordinal))
                {
                    // この巡回が記録した一時障害だけを、全確認の成功後に解除します。起動自体の失敗は残します。
                    runtime.StartupProblem = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                runtime.StartupProblem ??= ReconciliationFailure;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

}
