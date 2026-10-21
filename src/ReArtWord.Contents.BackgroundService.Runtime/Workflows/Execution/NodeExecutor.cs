using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Execution.Overlap;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>所有が確定したノードのローカル実行、手動取消、障害記録を担当します。</summary>
public sealed class NodeExecutor
{
    private readonly NodeRegistry registry;
    private readonly IPeerDirectory peers;
    private readonly SourceSupervisor sources;
    private readonly NodeSafetyGate safety;
    private readonly IWorkflowLog? log;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();
    private readonly NodeOverlapController overlap = new();
    private readonly Lock cancellationGate = new();
    private readonly Dictionary<Guid, DateTimeOffset> cancellationIntents = [];

    /// <summary>実行Peerの境界と、継続入力の管理先を指定します。</summary>
    public NodeExecutor(NodeRegistry registry, IPeerDirectory peers, SourceSupervisor sources, NodeSafetyGate safety, IWorkflowLog? log = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(safety);
        this.registry = registry;
        this.peers = peers;
        this.sources = sources;
        this.safety = safety;
        this.log = log;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>このPeerで実際に追跡している未完了の有限実行です。</summary>
    public IReadOnlyList<Guid> Running => running.Keys.ToArray();

    /// <summary>処理中nullと確認済み成否を状態画面へ渡します。</summary>
    public NodeValueObservations ValueObservations
    {
        get;
    } = new();

    /// <summary>ノードの実開始と確認した終了結果を通知する先。</summary>
    public IWorkflowEvents? Events
    {
        get; set;
    }

    /// <summary>更新などの明示管理操作による、新規通常実行の保留理由です。</summary>
    public Func<string?>? AdmissionCheck
    {
        get; set;
    }

    /// <summary>明示離脱中のローカル操作許可です。</summary>
    public DetachedOperationOptions DetachedOptions
    {
        get; set;
    } = new();

    /// <summary>実開始時に当該ノードが依存する未解消競合だけを確認します。</summary>
    public Func<string, NodeDefinition, string?>? DependencyCheck
    {
        get; set;
    }

    /// <summary>明示手動ノード実行にも同じ共通設定版を固定します。</summary>
    public IClusterOperationalProvider? OperationalProvider
    {
        get; set;
    }

    /// <summary>保護済みの生通信記録を実装する境界です。</summary>
    public INodeTrace? Trace
    {
        get; set;
    }

    /// <summary>実行地点のfootprint確認とクラスタ共通ファイル排他を通す境界です。</summary>
    public Func<IActionNode, NodeExecutionContext, CancellationToken, ValueTask<NodeResult>>? FileExecutionHandler
    {
        get; set;
    }

    /// <summary>取消要求を伝えます。実停止したかは元の実行結果で確認します。</summary>
    /// <returns>対象となる未完了実行があったかを示します。</returns>
    public async ValueTask<bool> RequestCancellationAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!running.TryGetValue(executionId, out CancellationTokenSource? cancellation))
        {
            return false;
        }
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>到着前の明示Source取消を元の開始期限まで有限保持します。</summary>
    /// <returns>取消要求を受け付けたか。実停止保証ではありません。</returns>
    public async ValueTask<bool> RequestCancellationAsync(Guid executionId, DateTimeOffset startBy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? active;
        using (cancellationGate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            foreach (Guid expired in cancellationIntents.Where(item => item.Value <= now).Select(static item => item.Key).ToArray())
            {
                cancellationIntents.Remove(expired);
            }
            if (!running.TryGetValue(executionId, out active))
            {
                if (startBy <= now || executionId == Guid.Empty || 1024 <= cancellationIntents.Count && !cancellationIntents.ContainsKey(executionId))
                {
                    return false;
                }
                cancellationIntents.TryAdd(executionId, startBy);
                return true;
            }
        }
        try
        {
            await active.CancelAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>実体と設定を検証し、ノードを一度だけ呼び出します。</summary>
    /// <returns>ノードが確認した結果です。</returns>
    // 抑制理由: 開始前検証や完了通知の例外にもノード識別子を残し、元の失敗を呼出元へ返します。
    [SuppressMessage("SourceQuality", "RAW330", Justification = "ノードの前後処理で失われていた診断を記録し、既存の例外伝播は維持します。")]
    public async Task<NodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.ExpiresAt <= clock.GetUtcNow() && !running.ContainsKey(request.Id))
            {
                return NodeResult.Failure("execution.expired", "未開始の要求は元の受付期限を過ぎています。", EOutcome.NotExecuted);
            }
            if (request.Operational is null && OperationalProvider is not null)
            {
                request = request with { Operational = OperationalProvider.Capture() };
                if (request.Operational is null)
                {
                    return NodeResult.Failure("configuration.unavailable", "共通運用版を取得できません。", EOutcome.NotExecuted);
                }
            }
            string? held = AdmissionCheck?.Invoke();
            if (held is not null)
            {
                return NodeResult.Failure("execution.maintenance", held, EOutcome.NotExecuted);
            }
            if (!string.Equals(request.TargetPeerId, peers.LocalPeerId, StringComparison.Ordinal) || request.TargetInstanceId != peers.LocalInstanceId)
            {
                return NodeResult.Failure("execution.target", "要求の実行Peer実体が一致しません。", EOutcome.NotExecuted);
            }
            INode? node = registry.Find(request.Node.Type);
            if (node is null)
            {
                return NodeResult.Failure("node.unknown", "登録されていないノードです。", EOutcome.NotExecuted);
            }
            if (NodeBehaviorContracts.Validate(request.Node, node.Descriptor) is { } incompatible)
            {
                return NodeResult.Failure(incompatible.Code, incompatible.Message, EOutcome.NotExecuted);
            }
            IReadOnlyList<ValidationIssue> issues = NodeSchemaValidator.Validate(request.Node, node.Descriptor);
            if (!Enum.IsDefined(request.Node.Overlap) || issues.Count != 0 || node.Validate(request.Node).Count != 0 || ConfiguredNodePorts.ResolveExecution(node.Descriptor, request.Node)!.Inputs.Any(port => port.Required && !request.Inputs.ContainsKey(port.Name)))
            {
                return NodeResult.Failure("node.invalid", "設定または必須入力が実行条件を満たしません。", EOutcome.NotExecuted);
            }
            if (node is ISourceNode)
            {
                return await sources.StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
            NodeResult result = await ExecuteActionAsync((IActionNode)node, request, cancellationToken).ConfigureAwait(false);
            if (CompletionPorts.Supports(node.Descriptor) && !IterationNodeTypes.IsStart(request.Node.Type) && !IterationNodeTypes.IsEnd(request.Node.Type))
            {
                ValueObservations.Complete(request, result, clock.GetUtcNow());
            }
            await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "node.completed", clock.GetUtcNow(), peers.LocalPeerId,
                ContractJson.ToValue(new { request.Id, request.Revision, result }), request.WorkflowId, request.Node.Id, request.CausationId, peers.LocalInstanceId, request.Hooks), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException && (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            await NodeExecutionLog.WriteResultAsync(log, peers.LocalPeerId, clock, request,
                NodeResult.Failure("execution.boundaryException", "ノードの開始前検証または完了処理を確認できません。", EOutcome.Unknown), false, error, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>明示停止中の入力を扱い、開始後の不明な停止を成功へ読み替えません。</summary>
    /// <returns>一つの有限処理の結果です。</returns>
    // 抑制理由: 拡張ノードの境界で障害を結果化し、別経路の実行を継続します。
    [SuppressMessage("Design", "CA1031", Justification = "任意のノードが投げる例外を不明結果に変換し、その本文による秘密の流出を防ぎます。")]
    private async Task<NodeResult> ExecuteActionAsync(IActionNode node, RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (node.Descriptor.HasAsyncCompletion)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool cancellationRequested;
        using (cancellationGate.EnterScope())
        {
            if (!running.TryAdd(request.Id, lifetime))
            {
                return NodeResult.Failure("execution.running", "同じ要求の実行は進行中です。耐久操作の結果を照会してください。", EOutcome.Unknown);
            }
            cancellationRequested = cancellationIntents.Remove(request.Id, out _);
        }
        bool started = false;
        NodeResult result;
        Exception? failure = null;
        try
        {
            if (cancellationRequested)
            {
                return NodeResult.Failure("execution.cancelledBeforeStart", "元Source入力の明示取消を開始前に受け付けました。", EOutcome.NotExecuted);
            }
            NodeResult? stopped = await safety.WaitAsync(request.WorkflowId, request.Node, lifetime.Token).ConfigureAwait(false);
            if (stopped is not null)
            {
                return stopped;
            }
            string? held = AdmissionCheck?.Invoke();
            if (held is not null)
            {
                return NodeResult.Failure("execution.maintenance", held, EOutcome.NotExecuted);
            }
            TimeZoneInfo zone = TimeZoneInfo.Utc;
            string? zoneId = request.Operational?.Settings.TimeZoneId ?? request.TimeZoneId;
            if (zoneId is not null)
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            NodeExecutionContext context = new(request.Id, request.CausationId, request.WorkflowId, request.Revision, request.Node.Id,
                peers.LocalPeerId, request.Node.Settings, request.Inputs, clock, zone)
            {
                Log = log,
                ContainsPersonalData = request.ContainsPersonalData,
                Operational = request.Operational,
                Progress = request.Progress,
                SourceScopes = request.SourceScopes,
                IsValueRead = request.IsValueRead,
                HostId = peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId)?.HostId,
                TracePolicy = new(request.Trace, request.TraceSelection),
                Trace = Trace,
                Hooks = request.Hooks,
                ContractVersion = NodeBehaviorContracts.Expected(request.Node),
            };
            await NodeExecutionLog.WriteInputsAsync(log, peers.LocalPeerId, clock, request, CancellationToken.None).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            result = await overlap.ExecuteAsync(request.WorkflowId, request.Node.Id, peers.LocalPeerId, request.Node.Overlap, async token =>
            {
                string? maintenance = AdmissionCheck?.Invoke();
                if (maintenance is not null)
                {
                    return NodeResult.Failure("execution.maintenance", maintenance, EOutcome.NotExecuted);
                }
                NodeResult? heldInput = await safety.WaitAsync(request.WorkflowId, request.Node, token).ConfigureAwait(false);
                if (heldInput is not null)
                {
                    return heldInput;
                }
                maintenance = AdmissionCheck?.Invoke();
                if (maintenance is not null)
                {
                    return NodeResult.Failure("execution.maintenance", maintenance, EOutcome.NotExecuted);
                }
                string? problem = OperationalDependencyPolicy.GetProblem(context.Operational, request.Node, node.Descriptor, node is IFileFootprintNode)
                    ?? DependencyCheck?.Invoke(request.WorkflowId, request.Node);
                if (problem is null && peers.GetPeers().Any(peer => peer.InstanceId == peers.LocalInstanceId && peer.Status == EPeerStatus.Detached))
                {
                    problem = await DetachedOperationPolicy.GetProblemAsync(node, context, DetachedOptions, token).ConfigureAwait(false);
                }
                if (problem is not null)
                {
                    return NodeResult.Failure("execution.operationDenied", problem, EOutcome.NotExecuted);
                }
                token.ThrowIfCancellationRequested();
                started = true;
                if (CompletionPorts.Supports(node.Descriptor) && !IterationNodeTypes.IsStart(request.Node.Type) && !IterationNodeTypes.IsEnd(request.Node.Type))
                {
                    ValueObservations.Begin(request, clock.GetUtcNow());
                }
                await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "node.started", clock.GetUtcNow(), peers.LocalPeerId,
                    ContractJson.ToValue(new { request.Id, request.Revision }), request.WorkflowId, request.Node.Id, request.CausationId, peers.LocalInstanceId, request.Hooks), CancellationToken.None).ConfigureAwait(false);
                if (node is IFileFootprintNode)
                {
                    if (FileExecutionHandler is null)
                    {
                        return NodeResult.Failure("file.gateMissing", "ファイルの共有排他が構成されていません。", EOutcome.NotExecuted);
                    }
                    return await FileExecutionHandler(node, context, token).ConfigureAwait(false);
                }
                return await node.ExecuteAsync(context, token).ConfigureAwait(false);
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (started)
            {
                outcome = EOutcome.Unknown;
            }
            result = NodeResult.Failure("execution.cancelUnconfirmed", "取消要求後の副作用の完了状態を確認できません。", outcome);
        }
        catch (Exception error)
        {
            failure = error;
            EOutcome outcome = EOutcome.NotExecuted;
            if (started)
            {
                outcome = EOutcome.Unknown;
            }
            result = NodeResult.Failure("execution.exception", "処理が例外で終了したため、実行状態の確認が必要です。", outcome);
        }
        finally
        {
            running.TryRemove(request.Id, out _);
        }
        await NodeExecutionLog.WriteResultAsync(log, peers.LocalPeerId, clock, request, result, node is IFileFootprintNode, failure, CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
