using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Retention;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows;

/// <summary>入力の一件ごとに独立した依存グラフを指定Peer上で実行します。</summary>
public sealed class WorkflowEngine
{
    private readonly NodeRegistry registry;
    private readonly IPeerDirectory directory;
    private readonly WorkflowNodeDispatch delivery;
    private readonly TimeProvider timeProvider;
    private readonly WorkflowNodeEvaluation evaluation;
    private readonly ExecutionReportCache completed = new();
    private readonly WorkflowExecutionOwnership ownership;
    private readonly NodeOutputRetentionQueue retentionQueue = new();

    /// <summary>異なる受信Peerからも同じ制限枠を使用する調停先です。</summary>
    public IWorkflowConcurrencyLimiter? ConcurrencyLimiter
    {
        get; set;
    }

    /// <summary>実際にProgressを保持したPeerだけへ終了を通知する境界です。</summary>
    public Func<Contracts.Management.Preview.ProgressSession, IReadOnlyList<Contracts.Cluster.PeerInfo>, CancellationToken, Task>? CompleteProgress
    {
        get => ownership.CompleteProgress;
        set => ownership.CompleteProgress = value;
    }

    /// <summary>グラフが所有する反復全体の処理中・確定成否です。</summary>
    public NodeValueObservations ValueObservations
    {
        get;
    } = new();

    /// <summary>明示予約された次の一件だけの、永続ログと分離した診断です。</summary>
    public WorkflowDebugRecorder Debug
    {
        get;
    }

    /// <summary>予定入力を保持している間、対象ごとの最新回と復帰待ちを管理します。</summary>
    internal Scheduling.ScheduledTargetReadiness ScheduledTargets
    {
        get;
    }

    private static TimeSpan AdmissionLifetime => TimeSpan.FromMinutes(5);

    /// <summary>一件の参加集合と実行PCを固定する共通処理です。</summary>
    public WorkflowTargetPlanner Targets
    {
        get;
    }

    /// <summary>開始・終了と対象別結果を共通Hookへ通知します。</summary>
    public IWorkflowEvents? Events
    {
        get => ownership.Events;
        set => ownership.Events = value;
    }

    /// <summary>予定入力の配送開始を、保持記録の比較更新で許可する処理。</summary>
    public Func<RemoteExecutionRequest, CancellationToken, ValueTask<NodeResult?>>? BeforeScheduledDispatch
    {
        get => delivery.BeforeScheduledDispatch;
        set => delivery.BeforeScheduledDispatch = value;
    }

    /// <summary>待受範囲を保持できない旧実行先へ、所有情報を捨てて配送しないための確認です。</summary>
    public Func<RemoteExecutionRequest, CancellationToken, ValueTask<NodeResult?>>? ScopedDispatchCheck
    {
        get => delivery.ScopedDispatchCheck;
        set => delivery.ScopedDispatchCheck = value;
    }

    /// <summary>予定入力の実行結果を保持先へ通知します。</summary>
    public Func<ExecutionReport, CancellationToken, ValueTask>? ScheduledResult
    {
        get => ownership.ScheduledResult;
        set => ownership.ScheduledResult = value;
    }

    /// <summary>実行前に置換・拒否された予定入力の結果を通知します。</summary>
    public Action<NodeEmission, NodeResult>? ScheduledInputRejected
    {
        get; set;
    }

    /// <summary>日時入力の取消を、担当交代時に再開しないよう記録します。</summary>
    public Func<Guid, CancellationToken, ValueTask<bool>>? CancelScheduledInput
    {
        get => ownership.CancelScheduledInput;
        set => ownership.CancelScheduledInput = value;
    }

    /// <summary>保持済み予定を重複してローカルキューへ戻さないために、追跡中かを確認します。</summary>
    /// <returns>待機または実行中ならtrue。</returns>
    public bool HasScheduledInput(Guid id) => ScheduledTargets.Contains(id);

    /// <summary>明示取消を元のPeer実体へ配送します。受付と実停止は区別します。</summary>
    public Func<RemoteExecutionRequest, CancellationToken, Task<bool>>? NodeCancellationHandler
    {
        get => ownership.CancelNode;
        set => ownership.CancelNode = value;
    }

    /// <summary>元の一件から発生した配送だけへ明示取消を要求します。</summary>
    /// <returns>取消配送の受付確認です。</returns>
    public Task<bool> RequestCancellationAsync(Guid causationId, CancellationToken cancellationToken = default)
        => ownership.CancelAsync(causationId, cancellationToken);

    /// <summary>補償可能なファイル操作を全対象一括で開始する境界です。</summary>
    public IFileTransactionDispatcher? FileTransactions
    {
        get => delivery.FileTransactions;
        set => delivery.FileTransactions = value;
    }

    /// <summary>開始時に一度だけ共通運用版を取得します。</summary>
    public IClusterOperationalProvider? OperationalProvider
    {
        get; set;
    }

    /// <summary>一件の開始時に保存済み方針と一時追跡を合成します。</summary>
    public ITracePolicyProvider? TracePolicyProvider
    {
        get; set;
    }

    /// <summary>開始信号で固定する値を、別の受信・実行にも共有する保存先です。</summary>
    public State.INodeStateStore? RetainedValues
    {
        get; set;
    }

    /// <summary>副作用を実行する配送境界とノードカタログを指定します。</summary>
    public WorkflowEngine(NodeRegistry registry, IPeerDirectory directory, IRemoteNodeDispatcher dispatcher, TimeProvider? timeProvider = null, TimeZoneInfo? timeZone = null, IWorkflowLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(directory);
        this.directory = directory;
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.registry = registry;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Debug = new(directory, registry, this.timeProvider);
        Targets = new WorkflowTargetPlanner(registry, directory, this.timeProvider);
        ownership = new(directory.LocalPeerId, directory.LocalInstanceId, this.timeProvider, log);
        ScheduledTargets = new(directory, this.timeProvider, AdmissionLifetime);
        ownership.Completed = ScheduledTargets.Release;
        delivery = new(dispatcher, ownership, ScheduledTargets, log, directory.LocalPeerId, this.timeProvider);
        evaluation = new(registry, directory, delivery, ownership, this.timeProvider, timeZone ?? TimeZoneInfo.Utc, AdmissionLifetime);
    }

    /// <summary>明示した入口、または入力接続のない有限ノードから一件を開始します。</summary>
    /// <returns>各ノードと実行先の実行結果です。</returns>
    public Task<ExecutionReport> ExecuteAsync(
        CompiledWorkflow workflow,
        string? entryNodeId = null,
        IReadOnlyDictionary<string, NodeValue>? inputs = null,
        Guid? executionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Definition.IsDashboard)
        {
            throw new InvalidOperationException("WFDashboard is a reference layout and cannot be executed.");
        }
        HashSet<string> entries = WorkflowEntryPoints.Select(workflow, registry, entryNodeId);
        TracePolicy saved = new(workflow.Definition.Trace, workflow.Definition.TraceSelection);
        TracePolicy trace = TracePolicyProvider?.GetPolicy(workflow.Definition.Id, saved) ?? saved;
        Guid id = executionId ?? Guid.NewGuid();
        return ownership.RunAsync(id, workflow.Definition.Id, workflow.Revision, () => RunAsync(workflow, entries, inputs, null, id, null, OperationalProvider?.Capture(), trace, null, null, cancellationToken),
            containsPersonalData: workflow.ExecutionDefinition.ContainsPersonalData, cancellationToken: cancellationToken);
    }

    /// <summary>継続入力の取得開始時の定義で下流の一件を開始します。</summary>
    /// <returns>継続入力自身と下流の結果です。</returns>
    public Task<ExecutionReport> ExecuteEmissionAsync(CompiledWorkflow workflow, NodeEmission emission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(emission);
        if (!string.Equals(workflow.Revision, emission.Context.Revision, StringComparison.Ordinal) || !workflow.Incoming.ContainsKey(emission.Context.NodeId))
        {
            throw new ArgumentException("取得開始時のグラフと入力が一致しません。", nameof(emission));
        }
        if (emission.Context.Schedule is not null && emission.Context.CapturedTargets is null)
        {
            emission = emission with { Context = emission.Context with { CapturedTargets = Targets.Capture(workflow, emission.Context.CapturedPeer, emission.Context.Operational) } };
        }
        if (!ScheduledTargets.Track(emission))
        {
            throw new InvalidOperationException("時間指定の対象待ちの保持上限に達したため、入力を開始しません。");
        }
        ExecutionFrame seed = new(emission.Context.NodeId, emission.Result.Outcome, [new TargetNodeResult(emission.Context.PeerId, emission.WorkflowResult())], SourceCompletion: emission.IsTerminal, SignalId: emission.Context.CausationId);
        TracePolicy trace = emission.Context.TracePolicy ?? new(workflow.Definition.Trace, workflow.Definition.TraceSelection);
        return ownership.RunAsync(emission.Context.CausationId, workflow.Definition.Id, workflow.Revision, () => RunAsync(workflow, [], null, seed, emission.Context.CausationId,
            emission.Context.CapturedPeer, emission.Context.Operational, trace, emission.Context.CapturedTargets, null, cancellationToken), emission.Context.Hooks, emission.Context.Schedule,
            containsPersonalData: workflow.ExecutionDefinition.ContainsPersonalData, sourceScopes: emission.Context.SourceScopes, cancellationToken: cancellationToken);
    }

    /// <summary>一件の確認済み結果を問い合わせます。</summary>
    /// <returns>保持中の結果、または未完了・保持範囲外ならnullです。</returns>
    public ExecutionReport? Find(Guid id) => completed.Find(id);

    /// <summary>手動実行と、Sourceから受け付けて実行中の一件を取得します。</summary>
    /// <returns>このPeer実体が管理する有限実行。</returns>
    public IReadOnlyList<WorkflowRunStatus> Active() => ownership.Active();

    /// <summary>最近の実行結果を取得します。</summary>
    /// <returns>新しい順の結果です。</returns>
    public IReadOnlyList<ExecutionReport> Recent()
    {
        return completed.Recent();
    }

    /// <summary>詳細を保持できなかった実行も、確定した結果の要約を返します。</summary>
    /// <returns>容量を制限した新しい順の履歴です。</returns>
    public IReadOnlyList<ExecutionResultView> RecentViews() => completed.RecentViews();

    /// <summary>親の完了を待つタスクをすべて追跡し独立経路を並行実行します。</summary>
    /// <returns>入力の一件の実行全体の結果です。</returns>
    // 抑制理由: 診断の未完了理由を残し、元の実行例外は既存の所有境界へそのまま渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "診断のために実行例外を置換または成功化しません。")]
    private async Task<ExecutionReport> RunAsync(
        CompiledWorkflow workflow,
        HashSet<string> entries,
        IReadOnlyDictionary<string, NodeValue>? entryInputs,
        ExecutionFrame? seed,
        Guid id,
        Contracts.Cluster.PeerInfo? sourcePeer,
        ClusterOperationalSnapshot? operational,
        TracePolicy trace,
        IReadOnlyDictionary<string, TargetResolution>? capturedTargets,
        WorkflowEventContext? context,
        CancellationToken cancellationToken = default)
    {
        // 明示取消は保存待ちへ伝えますが、開始済み遠隔処理の結果照会は元の寿命で完了を確認します。
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownership.Find(id)?.Token ?? CancellationToken.None, context?.Cancellation ?? CancellationToken.None);
        DateTimeOffset started = timeProvider.GetUtcNow();
        if (workflow.DependencyIssues.Count != 0 || OperationalProvider is not null && operational is null)
        {
            return new ExecutionReport(id, workflow.Definition.Id, workflow.Revision, EOutcome.NotExecuted, started, started,
                workflow.OrderedNodes.Select(static node => new NodeExecutionReport(node.Id, EOutcome.NotExecuted,
                    [new TargetNodeResult(string.Empty, NodeResult.Failure("configuration.unavailable", "The operational settings or a referenced recipe are unavailable; this execution was not started.", EOutcome.NotExecuted))])).ToArray());
        }
        IReadOnlyDictionary<string, TargetResolution> targets = capturedTargets ?? Targets.Capture(workflow, sourcePeer, operational);
        PcSelectionResolver selection = (targets as WorkflowTargetDictionary ?? (WorkflowTargetDictionary)Targets.Capture(workflow, sourcePeer, operational)).Resolver;
        WorkflowDebugCapture? capture = context?.Debug;
        if (context is null && seed?.SourceCompletion != true && seed?.SourceActivation != true)
        {
            capture = await Debug.BeginAsync(workflow, id, execution.Token).ConfigureAwait(false);
        }
        if (seed is not null)
        {
            capture?.Source(seed, id);
        }
        WorkflowDemandRunner demand = new(registry, workflow, targets,
            (node, parents, entry, initial, valueRead, scope) => capture?.ExecuteAsync(node, parents, id, initial, entry, valueRead,
                () => evaluation.ExecuteAsync(workflow, node, targets[node.Id], selection, parents.Select(Task.FromResult).ToArray(), entry, initial, valueRead, id, operational, trace, scope, cancellationToken), cancellationToken)
                ?? evaluation.ExecuteAsync(workflow, node, targets[node.Id], selection, parents.Select(Task.FromResult).ToArray(), entry, initial, valueRead, id, operational, trace, scope, cancellationToken), RetainedValues,
            (signal, inherited) =>
            {
                capture?.Child(id, signal.SignalId);
                return ownership.RunAsync(signal.SignalId, workflow.Definition.Id, workflow.Revision,
                    () => RunAsync(workflow, [], null, signal, signal.SignalId, sourcePeer, operational, trace, targets, inherited, cancellationToken), ownership.Find(id)?.Hooks, parentId: id,
                    containsPersonalData: workflow.ExecutionDefinition.ContainsPersonalData, cancellationToken: cancellationToken);
            },
            ValueObservations, id, directory.LocalPeerId, retentionQueue, context, capture, ownership.Find(id)?.Progress,
            new WorkflowConcurrencyExecution(ConcurrencyLimiter, directory, workflow.Definition.Id, id, execution.Cancel));
        ExecutionFrame[] frames;
        try
        {
            await using (demand.ConfigureAwait(false))
            {
                frames = await demand.ExecuteAsync(entries, entryInputs, seed,
                    () => execution.IsCancellationRequested || ownership.Find(id)?.IsCancellationRequested == true, timeProvider, execution.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (context is null)
            {
                capture?.Complete("debug.executionUnconfirmed", EOutcome.Unknown);
            }
            throw;
        }
        EOutcome outcome = ExecutionOutcomes.AggregateWorkflow(frames);
        if (outcome == EOutcome.Succeeded && ownership.Find(id)?.PreventedExecution(frames) == true)
        {
            outcome = EOutcome.Cancelled;
        }
        ExecutionReport report = new(id, workflow.Definition.Id, workflow.Revision, outcome, started, timeProvider.GetUtcNow(),
            frames.Select(frame => new NodeExecutionReport(frame.NodeId, frame.Outcome, frame.Targets, frame.FileTransaction, frame.Iteration, frame.PcSelection ?? targets[frame.NodeId].Selection)).ToArray());
        capture?.Results(id, frames);
        if (context is null)
        {
            capture?.Complete(outcome: outcome);
        }
        completed.Remember(report);
        return report;
    }

}
