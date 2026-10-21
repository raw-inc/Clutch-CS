using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>待受一つの実行と、一件の文脈・終了通知の固定を担当します。</summary>
internal sealed class SourceRunner
{
    private readonly IPeerDirectory peers;
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo timeZone;
    private readonly Func<string, CompiledWorkflow?> getEffective;
    private readonly Func<Guid, NodeEmission, CancellationToken, ValueTask> emit;
    private readonly CancellationToken stoppingToken;
    private readonly IWorkflowLog? log;
    private readonly SourceSelection selection;
    private readonly WorkflowTargetPlanner targets;
    private readonly Func<ITracePolicyProvider?> tracePolicyProvider;
    private readonly Func<INodeTrace?> trace;
    private readonly Func<string, NodeDefinition, NodeDescriptor, string?> admission;

    /// <summary>実際に確認した待受状態の通知先。</summary>
    internal IWorkflowEvents? Events
    {
        get; set;
    }

    /// <summary>元の待受に対応する時間指定の共有記録先。</summary>
    internal Scheduling.ISourceScheduleStore? ScheduleStore
    {
        get; set;
    }

    /// <summary>待受の所有元を確認し、下流へ引き継ぐ境界です。</summary>
    internal ISourceScopeControl? Scopes
    {
        get; set;
    }

    /// <summary>所有者の設定参照と、独立した下流受付を指定します。</summary>
    internal SourceRunner(IPeerDirectory peers, TimeProvider clock, TimeZoneInfo timeZone,
        Func<string, CompiledWorkflow?> getEffective, Func<Guid, NodeEmission, CancellationToken, ValueTask> emit, SourceSelection selection,
        Func<ITracePolicyProvider?> tracePolicyProvider, Func<INodeTrace?> trace, Func<string, NodeDefinition, NodeDescriptor, string?> admission, IWorkflowLog? log, WorkflowTargetPlanner targets, CancellationToken stoppingToken)
    {
        this.peers = peers;
        this.clock = clock;
        this.timeZone = timeZone;
        this.getEffective = getEffective;
        this.emit = emit;
        this.stoppingToken = stoppingToken;
        this.log = log;
        this.selection = selection;
        this.targets = targets;
        this.tracePolicyProvider = tracePolicyProvider;
        this.trace = trace;
        this.admission = admission;
    }

    /// <summary>元要求とローカル選択版のどちらの未解消項目も開始前に確認します。</summary>
    /// <returns>開始不可理由またはnullです。</returns>
    internal string? GetStartProblem(RemoteExecutionRequest request, ClusterOperationalSnapshot? operational, ISourceNode source)
    {
        return OperationalDependencyPolicy.GetProblem(request.Operational, request.Node, source.Descriptor, source is IFileFootprintNode)
            ?? OperationalDependencyPolicy.GetProblem(operational, request.Node, source.Descriptor, source is IFileFootprintNode)
            ?? admission(request.WorkflowId, request.Node, source.Descriptor);
    }

    /// <summary>開始受付の同じ変更境界で、現在の定義・運用版・実行先を再確認します。</summary>
    /// <returns>拒否理由、または固定できた要求と計画です。</returns>
    internal NodeResult? ValidateStart(RemoteExecutionRequest request, ISourceNode source, out RemoteExecutionRequest admitted, out CompiledWorkflow? plan)
    {
        admitted = request;
        plan = getEffective(request.WorkflowId);
        NodeDefinition? node = plan?.FindNode(request.Node.Id);
        if (plan is null || plan.DependencyIssues.Count != 0 || !string.Equals(plan.Revision, request.Revision, StringComparison.Ordinal) || node is null
            || !string.Equals(Contracts.Serialization.ContractJson.Hash(node), Contracts.Serialization.ContractJson.Hash(request.Node), StringComparison.Ordinal))
        {
            return NodeResult.Failure("source.revision", "開始受付中に設定版またはノード定義が変更されました。", EOutcome.NotExecuted);
        }
        ClusterOperationalSnapshot? operational = selection.Capture(request.Operational);
        PeerInfo? local = peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId);
        if (request.TargetInstanceId != peers.LocalInstanceId || !string.Equals(request.TargetPeerId, peers.LocalPeerId, StringComparison.Ordinal)
            || !(local is not null && (PeerStatusRules.CanParticipate(local.Status) || local.Status == EPeerStatus.Detached))
            || !selection.Contains(node, source.Descriptor, operational, local, request.WorkflowId))
        {
            return NodeResult.Failure("source.target", "現在の共通運用版でこの実体をSource対象として確認できません。", EOutcome.NotExecuted);
        }
        string? problem = GetStartProblem(request, operational, source);
        if (problem is not null)
        {
            return NodeResult.Failure("source.operationDenied", problem, EOutcome.NotExecuted);
        }
        admitted = request with { Operational = operational };
        return null;
    }

    /// <summary>取得開始時にグラフを保持し、待受の実タスクを完了まで追跡します。</summary>
    /// <returns>待受の実際の終了です。</returns>
    // 抑制理由: ノードの例外を失敗へ変換し、他の待受を停止させません。
    [SuppressMessage("Design", "CA1031", Justification = "拡張ノードの境界で予期しない例外を失敗結果へ変換し、秘密を含み得る本文を外へ出しません。")]
    internal async Task RunAsync(SourceSession session, ISourceNode source, CompiledWorkflow initialPlan, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Cancel.Token, cancellationToken);
        TimeSpan durationLimit = Timeout.InfiniteTimeSpan;
        if (session.Request.Node.ListenDurationMs is { } milliseconds)
        {
            durationLimit = TimeSpan.FromMilliseconds(milliseconds);
        }
        using CancellationTokenSource duration = new(durationLimit, clock);
        using CancellationTokenSource plannedStop = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token, duration.Token);
        NodeExecutionContext initial = Context(session.Request, initialPlan);
        SourceCaptureState captured = new(initial);
        Scheduling.INodeScheduleCursor? schedule = null;
        if (ScheduleStore is not null)
        {
            schedule = new Scheduling.SourceScheduleAccess(ScheduleStore, session.Request, () => session.Info);
        }
        NodeSourceContext context = new(initial, () => Capture(session, initialPlan, source, captured), (emission, token) =>
            {
                captured.Last = emission.Context;
                return EmitAsync(session, emission, cancellationToken: token);
            },
            () => plannedStop.IsCancellationRequested,
            async (status, token) =>
            {
                NodeSourceStatus previous = session.Info.Status;
                session.Update(status);
                if (previous != status)
                {
                    await PublishStatusAsync(session, initial.Hooks, token).ConfigureAwait(false);
                }
            }, plannedStop.Token) { Schedule = schedule, Signals = session.Signals?.Reader, Scopes = Scopes };
        NodeResult result;
        Exception? failure = null;
        try
        {
            NodeResult? refused = await SourceScopeAdmission.CheckStartAsync(Scopes, session.Request, lifetime.Token).ConfigureAwait(false);
            if (refused is not null)
            {
                result = refused;
            }
            else
            {
                plannedStop.Token.ThrowIfCancellationRequested();
                result = await source.RunAsync(context, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when ((captured.Ended || plannedStop.IsCancellationRequested) && !lifetime.IsCancellationRequested)
        {
            result = captured.Rejection ?? NodeResult.Success();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            result = NodeResult.Failure("source.cancelled", "待受の取消を確認しました。", EOutcome.Cancelled);
        }
        catch (Exception error)
        {
            failure = error;
            result = NodeResult.Failure("source.failed", "入力ノードを継続できません。設定と対象の状態を確認してください。");
        }
        if (captured.Ended && !lifetime.IsCancellationRequested)
        {
            result = captured.Rejection ?? NodeResult.Success();
        }
        ESourceState state = result.Outcome switch
        {
            EOutcome.Succeeded => ESourceState.Completed,
            EOutcome.Cancelled => ESourceState.Cancelled,
            _ => ESourceState.Failed,
        };
        string closedScope = "source.scope.closed";
        if (string.Equals(result.ReasonCode, closedScope, StringComparison.Ordinal))
        {
            state = ESourceState.Completed;
        }
        session.Update(new NodeSourceStatus(state, result.ReasonCode, result.Message), result: result);
        await PublishStatusAsync(session, captured.Last.Hooks, CancellationToken.None).ConfigureAwait(false);
        if (!stoppingToken.IsCancellationRequested)
        {
            NodeResult terminal = result;
            string success = "success";
            if (result.Outcome == EOutcome.Succeeded && string.Equals(result.Route, success, StringComparison.Ordinal))
            {
                terminal = result with { Route = "completed" };
            }
            NodeExecutionContext finalContext = captured.Last with { ExecutionId = Guid.NewGuid(), CausationId = Guid.NewGuid(), Schedule = null };
            await EmitAsync(session, new NodeEmission(finalContext, terminal, true), failure, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (failure is not null)
            {
                await NodeExecutionLog.WriteExceptionAsync(initial, result.ReasonCode ?? "source.failed", failure, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>待受の状態変化を、その起動元と通過済みHookを保持して通知します。</summary>
    /// <returns>状態通知の試行。</returns>
    private ValueTask PublishStatusAsync(SourceSession session, IReadOnlyList<WorkflowHookStep>? hooks, CancellationToken cancellationToken = default)
        => WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "source.status", clock.GetUtcNow(), peers.LocalPeerId,
            Contracts.Serialization.ContractJson.ToValue(session.Info), session.Request.WorkflowId, session.Request.Node.Id, session.Request.CausationId, peers.LocalInstanceId, hooks), cancellationToken);

    /// <summary>高頻度の正常入力は明示trace時だけ記録し、失敗は常時記録します。</summary>
    /// <returns>主処理の受付です。</returns>
    private async ValueTask EmitAsync(SourceSession session, NodeEmission emission, Exception? error = null, CancellationToken cancellationToken = default)
    {
        NodeExecutionContext context = emission.Context;
        Dictionary<string, NodeValue> outputs = new(emission.Result.Outputs, StringComparer.Ordinal)
        {
            ["source"] = NodeValue.FromObject(session.Info with { Revision = context.Revision, Result = null }),
        };
        emission = emission with { Result = emission.Result with { Outputs = outputs } };
        WorkflowDefinition? definition = context.CapturedWorkflow?.ExecutionDefinition;
        if (context.TracePolicy?.Enabled == true)
        {
            NodeValue data = NodeValue.FromObject(new { emission.Result.Outcome, emission.Result.Route, emission.IsTerminal });
            if (context.TracePolicy.Selection?.Outputs != false)
            {
                data = NodeValue.FromObject(new { emission.Result, emission.IsTerminal });
            }
            await WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), clock.GetUtcNow(), "DEBUG", peers.LocalPeerId,
                "Sourceの入力または終了結果を取得しました。", context.WorkflowId, context.NodeId, context.CausationId,
                data, definition?.ContainsPersonalData == true, Hooks: context.Hooks), CancellationToken.None).ConfigureAwait(false);
        }
        if (emission.Result.Outcome is EOutcome.Failed or EOutcome.Unknown)
        {
            await WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), clock.GetUtcNow(), "ERROR", peers.LocalPeerId,
                emission.Result.ReasonCode ?? "Sourceの結果を確認してください。", context.WorkflowId, context.NodeId, context.CausationId,
                NodeExecutionLog.Result(context.ExecutionId, context.Revision, emission.Result, error), definition?.ContainsPersonalData == true, Hooks: context.Hooks), CancellationToken.None).ConfigureAwait(false);
        }
        await emit(session.Request.Id, emission, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>次の一件が使う現在の有効グラフと入力を固定します。</summary>
    /// <returns>後続の処理完了まで旧版を保持する文脈です。</returns>
    private NodeExecutionContext Capture(SourceSession session, CompiledWorkflow initialPlan, ISourceNode source, SourceCaptureState captured)
    {
        CompiledWorkflow plan = getEffective(session.Request.WorkflowId) ?? initialPlan;
        NodeDefinition? node = plan.FindNode(session.Request.Node.Id);
        if (node is null || !string.Equals(SourceSelection.ConnectionVersion(node), SourceSelection.ConnectionVersion(session.Request.Node), StringComparison.Ordinal))
        {
            plan = initialPlan;
            node = session.Request.Node;
        }
        NodeDescriptor descriptor = source.Descriptor;
        ClusterOperationalSnapshot? operational = selection.Capture(session.Request.Operational);
        string? problem = OperationalDependencyPolicy.GetProblem(operational, node, descriptor, source is IFileFootprintNode)
            ?? admission(session.Request.WorkflowId, node, descriptor);
        if (problem is not null)
        {
            captured.Rejection = NodeResult.Failure("source.operationDenied", problem, EOutcome.NotExecuted);
            captured.Ended = true;
            session.RestartRequested = !session.HumanStopped;
            session.Stop.Cancel();
            throw new OperationCanceledException("Sourceの次入力は現在の許可条件を満たしません。", session.Stop.Token);
        }
        bool selected = selection.Contains(node, descriptor, operational, peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId), session.Request.WorkflowId);
        if (session.Stop.IsCancellationRequested || !selected)
        {
            captured.Ended = true;
            if (!selected && !session.HumanStopped)
            {
                session.RestartRequested = true;
            }
            session.Stop.Cancel();
            throw new OperationCanceledException("Sourceは新しい対象集合に含まれないため次入力を開始しません。", session.Stop.Token);
        }
        session.Started.TrySetResult(session.Info.Status);
        RemoteExecutionRequest request = session.Request with { Id = Guid.NewGuid(), CausationId = Guid.NewGuid(), Node = node, Revision = plan.Revision, Operational = operational };
        NodeExecutionContext result = Context(request, plan);
        captured.Last = result;
        return result;
    }

    /// <summary>場所・時刻・一件識別子を実行文脈へまとめます。</summary>
    /// <returns>ノードに渡す不変の文脈です。</returns>
    private NodeExecutionContext Context(RemoteExecutionRequest request, CompiledWorkflow plan)
    {
        ClusterOperationalSnapshot? operational = request.Operational;
        if (!selection.Available(operational))
        {
            throw new InvalidOperationException("共通運用版を取得できないため入力を開始しません。");
        }
        TimeZoneInfo zone = timeZone;
        if (operational is not null)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(operational.Settings.TimeZoneId);
        }
        TracePolicy saved = new(plan.Definition.Trace, plan.Definition.TraceSelection);
        IReadOnlyDictionary<string, NodeValue> inputs = plan.ConfigurationInputs?.GetValueOrDefault(request.Node.Id) ?? request.Inputs;
        Contracts.Cluster.PeerInfo? sourcePeer = peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId);
        return new NodeExecutionContext(request.Id, request.CausationId, request.WorkflowId, request.Revision, request.Node.Id,
            peers.LocalPeerId, request.Node.Settings, inputs, clock, zone)
        {
            Log = log,
            SourceScopes = request.SourceScopes,
            ContainsPersonalData = plan.ExecutionDefinition.ContainsPersonalData,
            Operational = operational,
            HostId = sourcePeer?.HostId,
            TracePolicy = tracePolicyProvider()?.GetPolicy(plan.Definition.Id, saved) ?? saved,
            Trace = trace(),
            CapturedWorkflow = plan,
            CapturedPeer = sourcePeer,
            CapturedTargets = targets.Capture(plan, sourcePeer, operational),
            Hooks = request.Hooks,
            ContractVersion = NodeBehaviorContracts.Expected(request.Node),
        };
    }

}
