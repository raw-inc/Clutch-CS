using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.CoreService.Files;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;
using ReArtWord.Contents.BackgroundService.CoreService.Updates;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

/// <summary>公開APIとワークフローが同じ保存・通信・実行境界を使用するよう接続します。</summary>
internal sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly RuntimeWorkflowActivation activation;
    private volatile bool stopping;
    private readonly ServiceReadiness readiness;

    /// <summary>一つのPeerで所有するサービス群を作成します。</summary>
    public ServiceRuntime(CommonConfiguration configuration, IDurableRepository repository, PeerCluster cluster, SecretStore secrets, IWorkflowLog log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(log);
        Configuration = configuration;
        Repository = repository;
        Cluster = cluster;
        Secrets = secrets;
        Events = new WorkflowEventHub(cluster.LocalPeerId, cluster.LocalInstanceId);
        Processes = new ProcessService(repository, cluster.LocalPeerId, configuration.Processes, events: Events, platform: new Windows.WindowsApplicationPlatform(), log: log);
        Resources = new ResourceService(repository, cluster.LocalPeerId, configuration.Resources, cluster.LocalInstanceId, configuration.Cluster.BindAddress);
        Maintenance = new ExecutionMaintenanceGate(repository, cluster.LocalPeerId);
        ManualSafety = new ManualSafetyService(cluster, repository);
        Operational = new ClusterConfigurationStore(repository, cluster);
        Trace = new TraceService(cluster, repository);
        ClusterStop = new ClusterStopService(cluster, repository, Trace);
        TraceRawLogger rawTrace = new(cluster, log, secrets);
        Persistence = new(configuration, repository, cluster);
        Operational.Availability = Persistence.Availability;
        Scripts = new(configuration.DataDirectory);
        (NodeRegistry registry, SourceEndBinding sourceEnd, Nodes.FileSystem.Transfers.FileTransferService files) = RuntimeNodeRegistration.Create(configuration, cluster, secrets, log, Events, Persistence.AvailableState, Processes, Resources, Previews, Scripts.Packages, Scripts.Runtimes);
        Registry = registry;
        Compiler = new WorkflowCompiler(Registry);
        FileTransactions = new FileTransactionBinding(Registry, cluster, repository, configuration.Files, files, log);
        FileTransactions.Transactions.DetachedOptions = configuration.Detached;
        Safety = new NodeSafetyGate(cluster);
        Safety.ManualStopCheck = ManualSafety.IsStopped;
        Safety.ManualContinueCheck = ManualSafety.IsContinued;
        RuntimeNodeDispatcher dispatcher = new(cluster);
        Engine = new WorkflowEngine(Registry, cluster, dispatcher, timeZone: configuration.TimeZone, log: log);
        Engine.ConcurrencyLimiter = new Infrastructure.Execution.Concurrency.ClusterWorkflowConcurrencyLimiter(cluster, repository);
        WorkflowProgressBinding.Register(cluster, Engine, Previews);
        Debug = new(cluster, Engine.Debug);
        Engine.RetainedValues = new RuntimeRetainedValues(cluster, Persistence.State);
        ExecutionJournal = new(repository, cluster.LocalPeerId, Engine);
        Engine.FileTransactions = FileTransactions.Transactions;
        Engine.OperationalProvider = Operational;
        Engine.TracePolicyProvider = Trace;
        Engine.Events = Events;
        Infrastructure.Scheduling.ScheduleCursorStore scheduleCursors = new(cluster, repository);
        Sources = new SourceSupervisor(Registry, cluster, Engine, Safety, timeZone: configuration.TimeZone, log: log, schedules: scheduleCursors);
        SourceScopeBinding sourceScopes = new(cluster);
        sourceScopes.Register(Sources);
        Sources.Scopes = sourceScopes;
        Engine.ScopedDispatchCheck = sourceScopes.CheckTargetAsync;
        Registry.Dynamic = new Nodes.Control.Subworkflows.SubworkflowCatalog(Registry, Sources.GetEffectiveWorkflows);
        ScheduledInputs = new(cluster, scheduleCursors.Inputs, Compiler, Engine, Sources);
        Sources.OperationalProvider = Operational;
        Sources.TracePolicyProvider = Trace;
        Sources.Trace = rawTrace;
        Sources.DetachedOptions = configuration.Detached;
        Sources.Events = Events;
        ManualSafety.AutomaticStopCheck = (workflowId, scope) => Safety.IsScopeAutomaticallyStopped(Sources.GetEffective(workflowId)?.Definition, scope);
        Executor = new NodeExecutor(Registry, cluster, Sources, Safety, log);
        Executor.OperationalProvider = Operational;
        Executor.Trace = rawTrace;
        Executor.Events = Events;
        Executor.DetachedOptions = configuration.Detached;
        Sources.AdmissionCheck = MaintenanceReason;
        Executor.AdmissionCheck = MaintenanceReason;
        Executor.FileExecutionHandler = FileTransactions.Transactions.ExecuteFileAsync;
        NodeCancellationBinding.Register(cluster, Engine, Executor);
        sourceEnd.Register(Sources);
        activation = new(this);
        Workflows = new WorkflowStore(repository, cluster, Compiler)
        {
            Availability = Persistence.Availability,
            CanEditDetached = () => configuration.Detached.Edit,
            ActivateHandler = activation.ApplyAsync,
            DeactivateHandler = activation.RemoveAsync,
            GetEffective = Sources.GetEffective,
        };
        Conflicts = new ClusterConflictService(repository, cluster, Workflows, Operational, Registry);
        cluster.SetConflictReadinessCheck(Conflicts.PrepareAsync);
        Executor.DependencyCheck = Conflicts.GetExecutionProblem;
        Sources.DependencyCheck = Conflicts.GetExecutionProblem;
        FileTransactions.Transactions.DependencyCheck = Conflicts.GetExecutionProblem;
        Snapshots = new SnapshotService(repository, cluster, Workflows);
        Snapshots.Events = Events;
        Snapshots.RegisterConfiguration(Operational.SnapshotParticipant);
        Operational.IsRestoreLocked = () => Snapshots.IsConfigurationLocked(ClusterConfigurationStore.Id);
        SnapshotCatalog = new SnapshotCatalog(repository, cluster);
        Cluster.RemoteExecutionHandler = Executor.ExecuteAsync;
        Cluster.UpdateHostGroups(configuration.HostGroups);
        readiness = new ServiceReadiness(this);
        Cluster.ReadinessCheck = readiness.CheckAsync;
        Cluster.LocalDetachedExecutionAllowed = request => DetachedManagementPolicy.AllowsRequest(request, configuration.Detached);
    }

    /// <summary>明示したプレビューノードの短命な取得値です。</summary>
    public NodePreviewStore Previews
    {
        get;
    } = new();

    /// <summary>解析と実行の環境、パッケージ導入を同じライフサイクルで所有します。</summary>
    public ScriptServices Scripts
    {
        get;
    }

    /// <summary>実行結果の非同期保存と再起動後の有限診断です。</summary>
    public WorkflowExecutionJournal ExecutionJournal
    {
        get;
    }

    /// <summary>クラスタ共通の区間占有と、非永続の待機一覧です。</summary>
    public Infrastructure.Execution.Concurrency.ClusterWorkflowConcurrencyLimiter Concurrency => (Infrastructure.Execution.Concurrency.ClusterWorkflowConcurrencyLimiter)Engine.ConcurrencyLimiter!;

    /// <summary>通常履歴と分離した、明示した次の一件だけのデバッグ記録です。</summary>
    public WorkflowDebugService Debug
    {
        get;
    }

    /// <summary>内容競合の用途別隔離と候補比較を管理します。</summary>
    public ClusterConflictService Conflicts
    {
        get;
    }

    /// <summary>ローカルの明示した起動設定です。</summary>
    public CommonConfiguration Configuration
    {
        get;
    }

    /// <summary>用途別の永続保存です。</summary>
    public IDurableRepository Repository
    {
        get;
    }

    /// <summary>同じUDP通信を用いるPeer参加管理です。</summary>
    public PeerCluster Cluster
    {
        get;
    }

    /// <summary>実行時だけ秘密値を解決します。</summary>
    public SecretStore Secrets
    {
        get;
    }

    /// <summary>通常ワークフロー向け事象配信です。</summary>
    public WorkflowEventHub Events
    {
        get;
    }

    /// <summary>登録したアプリケーションの操作と実生存確認です。</summary>
    public ProcessService Processes
    {
        get;
    }

    /// <summary>ワークフローと独立して診断履歴を採取します。</summary>
    public ResourceService Resources
    {
        get;
    }

    /// <summary>参加状態と分離した新規通常実行の保留です。</summary>
    public ExecutionMaintenanceGate Maintenance
    {
        get;
    }

    /// <summary>再起動後にも維持する明示停止です。</summary>
    public ManualSafetyService ManualSafety
    {
        get;
    }

    /// <summary>共有運用設定の保存候補と実有効版です。</summary>
    public ClusterConfigurationStore Operational
    {
        get;
    }

    /// <summary>保存済みの通常方針と一時診断を合成します。</summary>
    public TraceService Trace
    {
        get;
    }

    /// <summary>一時追跡の解除後に元の実体へ通常終了を要求します。</summary>
    public ClusterStopService ClusterStop
    {
        get;
    }

    /// <summary>独立Updaterからの要求をローカル更新へ接続します。</summary>
    public LocalUpdateAdapter? Updater
    {
        get; set;
    }

    /// <summary>汎用のローカル・共有状態です。</summary>
    public RuntimePersistence Persistence
    {
        get;
    }

    /// <summary>既存共有同期とスナップショットが使用する強い状態保存です。</summary>
    public NodeStateStore State => Persistence.State;

    /// <summary>有効なノードカタログです。</summary>
    public NodeRegistry Registry
    {
        get;
    }

    /// <summary>保存前に構造と意味を検証します。</summary>
    public WorkflowCompiler Compiler
    {
        get;
    }

    /// <summary>明示した安全停止を管理します。</summary>
    public NodeSafetyGate Safety
    {
        get;
    }

    /// <summary>一件のグラフを実行します。</summary>
    public WorkflowEngine Engine
    {
        get;
    }

    /// <summary>共有ファイル排他と全対象補償の実行境界です。</summary>
    public FileTransactionBinding FileTransactions
    {
        get;
    }

    /// <summary>有効版と継続入力の実タスクを管理します。</summary>
    public SourceSupervisor Sources
    {
        get;
    }

    /// <summary>保持済み予定の配送許可と未開始入力の引継ぎを管理します。</summary>
    public Infrastructure.Scheduling.ScheduledInputCoordinator ScheduledInputs
    {
        get;
    }

    /// <summary>有限ノードのローカル実行です。</summary>
    public NodeExecutor Executor
    {
        get;
    }

    /// <summary>共同編集・保存・共有・稼働反映です。</summary>
    public WorkflowStore Workflows
    {
        get;
    }

    /// <summary>設定と宣言した実行時状態のスナップショットです。</summary>
    public SnapshotService Snapshots
    {
        get;
    }

    /// <summary>保存済みスナップショットの照会です。</summary>
    public SnapshotCatalog SnapshotCatalog
    {
        get;
    }

    /// <summary>実際の管理待受開始をReady条件へ反映します。</summary>
    public bool ManagementStarted
    {
        get; set;
    }

    /// <summary>起動時の設定反映などで人の判断が必要な状態です。</summary>
    public string? StartupProblem
    {
        get; internal set;
    }

    /// <summary>起動時の必須境界を成立させられなかったことを終了コードへ反映します。</summary>
    public bool StartupFailed
    {
        get; internal set;
    }

    /// <summary>このPeerの現在の参加状態です。</summary>
    public EPeerStatus Status => Cluster.GetPeers().Single(peer => peer.InstanceId == Cluster.LocalInstanceId).Status;

    /// <summary>同期内容をローカル採用する段階の問題です。</summary>
    public string? ReadinessProblem => readiness.Problem;

    /// <summary>参加準備と初回共有設定導入を同じ状態で確認します。</summary>
    internal ServiceReadiness Readiness => readiness;

    /// <summary>再起動時にも保留を解除するまで通常実行を開始しません。</summary>
    /// <returns>保留理由、または通常実行可能なnullです。</returns>
    private string? MaintenanceReason() => readiness.MaintenanceReason(stopping);

    /// <summary>待受と一件処理の停止を確認して所有資源を解放します。</summary>
    /// <returns>ランタイムの終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        ManagementStarted = false;
        stopping = true;
        await Scripts.DisposeAsync().ConfigureAwait(false);
        foreach (Guid id in Executor.Running)
        {
            await Executor.RequestCancellationAsync(id).ConfigureAwait(false);
        }
        await ((IAsyncDisposable)Sources).DisposeAsync().ConfigureAwait(false);
        await Concurrency.DisposeAsync().ConfigureAwait(false);
        await ExecutionJournal.FlushAsync().ConfigureAwait(false);
        ScheduledInputs.Dispose();
        await FileTransactions.PrepareStopAsync().ConfigureAwait(false);
        await Cluster.StopAsync().ConfigureAwait(false);
        readiness.Dispose();
        Conflicts.Dispose();
        Processes.Dispose();
        Resources.Dispose();
        Maintenance.Dispose();
        ManualSafety.Dispose();
        Operational.Dispose();
        Trace.Dispose();
        Persistence.Dispose();
        FileTransactions.Dispose();
        activation.Dispose();
        ExecutionJournal.Dispose();
    }
}
