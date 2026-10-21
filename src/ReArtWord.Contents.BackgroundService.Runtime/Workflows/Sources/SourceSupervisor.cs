using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>有効な設定、待受の寿命、入力一件の設定版を管理します。</summary>
public sealed class SourceSupervisor : IAsyncDisposable
{
    private readonly NodeRegistry registry;
    private readonly IPeerDirectory peers;
    private readonly TimeProvider clock;
    private readonly SourceRunner runner;
    private readonly SourceSelection selection;
    private readonly SourceScopeAdmission scopeAdmission;
    private readonly ConcurrentDictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Workflow, string Node), SourceSession> sessions = new();
    private readonly ConcurrentQueue<SourceExecutionInfo> history = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly SourceEmissionQueue queue;
    private static TimeSpan StartupWait => TimeSpan.FromSeconds(10);
    private static TimeSpan AdmissionLifetime => TimeSpan.FromMinutes(5);

    /// <summary>ノードカタログと実配送を行うエンジンを接続します。</summary>
    public SourceSupervisor(NodeRegistry registry, IPeerDirectory peers, WorkflowEngine engine, NodeSafetyGate safety, TimeProvider? timeProvider = null, TimeZoneInfo? timeZone = null, IWorkflowLog? log = null, Scheduling.ISourceScheduleStore? schedules = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(safety);
        this.registry = registry;
        this.peers = peers;
        clock = timeProvider ?? TimeProvider.System;
        scopeAdmission = new(clock);
        queue = new SourceEmissionQueue(engine, safety);
        selection = new SourceSelection(peers, () => OperationalProvider, GetEffective, engine.Targets);
        runner = new SourceRunner(peers, clock, timeZone ?? TimeZoneInfo.Utc, GetEffective, queue.EnqueueAsync, selection, () => TracePolicyProvider, () => Trace, (id, node, descriptor) => DetachedOperationPolicy.GetSourceProblem(peers, DependencyCheck, DetachedOptions, id, node, descriptor), log, engine.Targets, stopping.Token);
        runner.ScheduleStore = schedules;
    }

    /// <summary>実際にこのPeerで反映済みの版を取得します。</summary>
    /// <returns>有効版、または未反映ならnullです。</returns>
    public CompiledWorkflow? GetEffective(string workflowId) => effective.GetValueOrDefault(workflowId);

    /// <summary>各待受の状態と実停止結果を取得します。</summary>
    /// <returns>現在および直近終了した待受です。</returns>
    public IReadOnlyList<SourceExecutionInfo> GetSources() => sessions.Values.Select(static session => session.Info).Concat(history).OrderByDescending(static item => item.StartedAt).ToArray();

    /// <summary>反映済みワークフローを、現在の担当確認に使用するため取得します。</summary>
    /// <returns>列挙中に変更されない計画の一覧。</returns>
    public IReadOnlyList<CompiledWorkflow> GetEffectiveWorkflows() => effective.Values.ToArray();

    /// <summary>実行エンジンへ渡す境界で結果化した直近の障害です。</summary>
    /// <returns>因果識別子ごとの確認結果です。</returns>
    public IReadOnlyDictionary<Guid, NodeResult> Failures => queue.Failures;

    /// <summary>待機と実行中に保持する値の容量です。</summary>
    public SourceQueueStatistics Statistics => queue.Statistics;

    /// <summary>退避経路が開始した待受の寿命を管理します。</summary>
    public ISourceScopeControl? Scopes
    {
        get => runner.Scopes;
        set => runner.Scopes = value;
    }

    /// <summary>待受状態の変化を共通Hookへ通知する先。</summary>
    public IWorkflowEvents? Events
    {
        get => runner.Events;
        set => runner.Events = value;
    }

    /// <summary>Sourceの次の一件にも現在の共通運用版を適用します。</summary>
    public Runtime.Configuration.IClusterOperationalProvider? OperationalProvider
    {
        get; set;
    }

    /// <summary>Sourceが取得する次の一件の追跡方針です。</summary>
    public ITracePolicyProvider? TracePolicyProvider
    {
        get; set;
    }

    /// <summary>保護済みの生通信記録を行う境界です。</summary>
    public INodeTrace? Trace
    {
        get; set;
    }

    /// <summary>新しい待受を保留し、既存待受へ正常停止を伝える共通理由です。</summary>
    public Func<string?>? AdmissionCheck
    {
        get; set;
    }

    /// <summary>明示離脱中の待受と次入力に適用する操作許可です。</summary>
    public DetachedOperationOptions DetachedOptions
    {
        get; set;
    } = new();

    /// <summary>開始時と次の一件で当該ノードの未解消依存だけを確認します。</summary>
    public Func<string, NodeDefinition, string?>? DependencyCheck
    {
        get; set;
    }

    /// <summary>新しい入力の受付を止めて有効版を除去します。開始済みの一件は保持した版で継続します。</summary>
    /// <returns>受付停止の通知と設定除去です。</returns>
    public async Task RemoveAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            effective.TryRemove(workflowId, out _);
            IReadOnlySet<string> affected = RecipeDependencyRefresh.Refresh(workflowId, effective, registry);
            foreach (SourceSession session in sessions.Values.Where(session => affected.Contains(session.Request.WorkflowId)))
            {
                session.RestartRequested = !string.Equals(session.Request.WorkflowId, workflowId, StringComparison.Ordinal);
                await session.Stop.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>保存・共有・反映許可の確認が完了した版を、次の入力から使用します。</summary>
    /// <returns>設定の反映と変更した待受の正常停止確認です。</returns>
    public async Task ApplyAsync(CompiledWorkflow workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlySet<string> affected = RecipeDependencyRefresh.Apply(workflow, effective, registry);

            // 有効版を採用済みのため、待受への反映通知を途中の取消しで取り残しません。
            foreach (SourceSession session in sessions.Values.Where(session => affected.Contains(session.Request.WorkflowId)))
            {
                await session.ApplyAsync(effective[session.Request.WorkflowId], CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            changes.Release();
        }
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所属・Coordinator・利用可能性に合わせて自動開始する待受を調整します。</summary>
    /// <returns>終了済み待受の片付けと開始要求の完了です。</returns>
    // 抑制理由: sessionsが所有する待受を参照し、一覧から除いた完了済み資源だけRetireで解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "実行中の待受は所有一覧に残し、このメソッドの終了では解放しません。")]
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        ClusterOperationalSnapshot? operational = selection.Capture();
        PeerInfo? local = peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId);
        IReadOnlySet<SourceScope> ended = await scopeAdmission.EndedAsync(Scopes, sessions.Values, cancellationToken).ConfigureAwait(false);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (KeyValuePair<(string Workflow, string Node), SourceSession> item in sessions)
            {
                SourceSession session = item.Value;
                CompiledWorkflow? plan = GetEffective(session.Request.WorkflowId);
                NodeDefinition? node = plan?.FindNode(session.Request.Node.Id);
                if (session.Completion.IsCompleted)
                {
                    if (node is null || registry.Find(node.Type) is not ISourceNode)
                    {
                        sessions.TryRemove(item.Key, out _);
                        Retire(session);
                    }
                    continue;
                }
                if (await session.StopIfScopeEndedAsync(ended, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                bool selected = node is not null && selection.Contains(node, registry.Find(node.Type)!.Descriptor, operational, local, session.Request.WorkflowId);
                if (node is not null && DetachedOperationPolicy.GetSourceProblem(peers, DependencyCheck, DetachedOptions, session.Request.WorkflowId, node, registry.Find(node.Type)!.Descriptor) is not null || AdmissionCheck?.Invoke() is not null || !(local is not null && (PeerStatusRules.CanParticipate(local.Status) || local.Status == EPeerStatus.Detached)) || plan?.Definition.Enabled != true || !selected)
                {
                    session.RestartRequested = true;
                    await session.Stop.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            changes.Release();
        }
        if (!selection.Available(operational) || AdmissionCheck?.Invoke() is not null || !(local is not null && (PeerStatusRules.CanParticipate(local.Status) || local.Status == EPeerStatus.Detached)))
        {
            return;
        }
        foreach (CompiledWorkflow plan in effective.Values.Where(static plan => plan.Definition.Enabled && plan.DependencyIssues.Count == 0 && !SubworkflowInterface.IsSubworkflow(plan.Definition)))
        {
            foreach (NodeDefinition node in plan.OrderedNodes.Where(node => SourceSelection.Automatic(plan, node, registry.Find(node.Type))))
            {
                (string Workflow, string Node) key = (plan.Definition.Id, node.Id);
                if (sessions.TryGetValue(key, out SourceSession? previous))
                {
                    if (!previous.Completion.IsCompleted || !previous.RestartRequested || previous.HumanStopped)
                    {
                        continue;
                    }
                }
                if (!selection.Contains(node, registry.Find(node.Type)!.Descriptor, operational, workflowId: plan.Definition.Id))
                {
                    continue;
                }
                RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), plan.Definition.Id, plan.Revision, node,
                    node.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal), peers.LocalPeerId, peers.LocalInstanceId, clock.GetUtcNow() + AdmissionLifetime, Operational: operational);
                _ = await StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>同じノードの待受を重複させず、実際の待受開始状態を返します。</summary>
    /// <returns>一件の成功とは別の待受受付結果です。</returns>
    // 抑制理由: 待受とそのタスクを管理一覧で所有し、実終了をawaitしてから資源を解放します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "待受タスクはSourceSession.Completionに保存し、StopAsyncとDisposeAsyncでawaitします。"),
     SuppressMessage("SourceQuality", "RAW141", Justification = "待受の寿命は呼出しを越えるため、所有権をsessionsへ移し終了後に解放します。")]
    public async Task<NodeResult> StartAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (registry.Find(request.Node.Type) is not ISourceNode source)
        {
            return NodeResult.Failure("source.type", "継続入力ノードではありません。", EOutcome.NotExecuted);
        }
        SourceSession session;
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? held = AdmissionCheck?.Invoke();
            if (held is not null)
            {
                return NodeResult.Failure("source.maintenance", held, EOutcome.NotExecuted);
            }
            NodeResult? invalid = runner.ValidateStart(request, source, out RemoteExecutionRequest admitted, out CompiledWorkflow? plan);
            if (invalid is not null)
            {
                return invalid;
            }
            request = admitted;
            (string Workflow, string Node) key = (request.WorkflowId, request.Node.Id);
            if (sessions.TryGetValue(key, out SourceSession? existing) && !existing.Completion.IsCompleted)
            {
                if (source is IResettableSourceNode reset && existing.TrySignal(reset.ResetInputs, request, clock.GetTimestamp()))
                {
                    return new NodeResult(EOutcome.Succeeded, "listening", new Dictionary<string, NodeValue>(StringComparer.Ordinal));
                }
                return NodeResult.Failure("source.alreadyRunning", "同じノードの待受が既に稼働中です。", EOutcome.NotExecuted);
            }
            if (existing is not null)
            {
                Retire(existing);
            }
            session = new SourceSession(request, clock.GetUtcNow(), source is IResettableSourceNode);
            sessions[key] = session;
            session.Completion = runner.RunAsync(session, source, plan!, CancellationToken.None);
        }
        finally
        {
            changes.Release();
        }
        NodeSourceStatus status;
        try
        {
            status = await session.Started.Task.WaitAsync(StartupWait, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new NodeResult(EOutcome.Succeeded, "starting", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["source"] = NodeValue.FromObject(session.Info) });
        }
        if (status.State != ESourceState.Listening)
        {
            return NodeResult.Failure(status.ReasonCode ?? "source.unavailable", "待受は利用可能な状態になっていません。状態表示で回復または終了を確認してください。", EOutcome.NotExecuted);
        }
        return new NodeResult(EOutcome.Succeeded, "listening", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["source"] = NodeValue.FromObject(session.Info) });
    }

    /// <summary>正常停止と緊急取消を区別し、呼出元が待つ間に実停止を確認します。</summary>
    /// <returns>停止確認結果です。確認を打ち切った場合はUnknownです。</returns>
    // 抑制理由: 所有一覧内の待受を停止し、資源は一覧からの除去時またはSupervisor終了時に解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "停止結果を後から確認するため、参照した待受の所有権はsessionsへ残します。")]
    public async Task<NodeResult> StopAsync(string workflowId, string nodeId, bool cancel = false, Guid? sourceId = null, bool waitForCompletion = true, CancellationToken cancellationToken = default)
    {
        SourceSession? session;
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.TryGetValue((workflowId, nodeId), out session))
            {
                return NodeResult.Failure("source.missing", "このPeerに待受はありません。", EOutcome.NotExecuted);
            }
            if (sourceId is not null && session.Request.Id != sourceId)
            {
                return NodeResult.Failure("source.instanceChanged", "指定された待受は現在の待受と異なります。再開後の待受は停止しません。", EOutcome.NotExecuted);
            }
            session.HumanStopped = true;
            session.RestartRequested = false;
            if (cancel)
            {
                queue.MarkSessionCancelled(session.Request.Id);
                await session.Cancel.CancelAsync().ConfigureAwait(false);
            }
            await session.Stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            changes.Release();
        }
        return await session.WaitForStopAsync(queue, cancel, waitForCompletion, runner.ScheduleStore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保持済みの予定入力を、取得時の内容のまま既存の受付へ渡します。</summary>
    /// <returns>通常の保持上限と停止方針に従う受付。</returns>
    public ValueTask ResumeScheduledAsync(Guid sourceId, NodeEmission emission, CancellationToken cancellationToken = default) => queue.EnqueueAsync(sourceId, emission, cancellationToken);

    /// <summary>指定した所有範囲で開始した待受だけを止め、受付済みの有限処理には取消を送りません。</summary>
    /// <returns>元の待受すべての終了を確認できたかです。</returns>
    public ValueTask<bool> EndScopeAsync(SourceScope scope, CancellationToken cancellationToken = default)
        => SourceScopeAdmission.EndAsync(scope, sessions.Values, (session, token) => StopAsync(session.Request.WorkflowId, session.Request.Node.Id,
            sourceId: session.Request.Id, cancellationToken: token), cancellationToken);

    /// <summary>完了済み待受は資源を解放し、直近の表示情報だけを有限保持します。</summary>
    private void Retire(SourceSession session)
    {
        queue.RetireSession(session.Request.Id);
        using (session)
        {
            session.ArchiveTo(history);
        }
    }

    /// <summary>受付と一件処理を止め、追跡中の実タスクを待って解放します。</summary>
    /// <returns>所有する処理の終了確認です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await ((IAsyncDisposable)scopeAdmission).DisposeAsync().ConfigureAwait(false);
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(sessions.Values.Select(static session => session.Completion)).ConfigureAwait(false);
        await ((IAsyncDisposable)queue).DisposeAsync().ConfigureAwait(false);
        foreach (SourceSession session in sessions.Values)
        {
            session.Dispose();
        }
        stopping.Dispose();
        changes.Dispose();
    }
}
