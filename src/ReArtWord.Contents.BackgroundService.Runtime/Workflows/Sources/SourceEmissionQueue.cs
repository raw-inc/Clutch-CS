using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>停止入力を実行枠から分け、独立したワークフローの受付を維持します。</summary>
internal sealed class SourceEmissionQueue : IAsyncDisposable
{
    private readonly WorkflowEngine engine;
    private readonly NodeSafetyGate safety;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly List<NodeEmission> ready = [];
    private readonly List<NodeEmission> held = [];
    private readonly ConcurrentDictionary<Guid, Task<EOutcome>> running = new();
    private readonly Dictionary<NodeEmission, Guid> owners = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Guid> cancelledSessions = [];
    private readonly ConcurrentDictionary<(string Workflow, string Peer, bool Terminal), int> active = new();
    private readonly ConcurrentDictionary<Guid, NodeResult> failures = new();
    private readonly Lazy<Task> consumer;
    private readonly SourcePayloadBudget budget = new();
    private readonly SourceQueueNotifications notifications = new();
    private static int MaximumPending => 512;
    private static int MaximumHeldPerNode => 128;
    private static int MaximumRunningPerScope => 64;

    /// <summary>独立した受付と有限の保持領域を開始します。</summary>
    internal SourceEmissionQueue(WorkflowEngine engine, NodeSafetyGate safety)
    {
        this.engine = engine;
        this.safety = safety;
        consumer = new Lazy<Task>(() => ConsumeAsync(stopping.Token));
        if (consumer.Value.IsFaulted)
        {
            throw new InvalidOperationException("入力受付を開始できません。");
        }
    }

    /// <summary>破棄・置換・容量拒否と実行境界の障害を表示します。</summary>
    internal IReadOnlyDictionary<Guid, NodeResult> Failures => new Dictionary<Guid, NodeResult>(failures);

    /// <summary>保持領域の現在値を同じ同期境界で読みます。</summary>
    internal SourceQueueStatistics Statistics
    {
        get
        {
            using (gate.EnterScope())
            {
                return new SourceQueueStatistics(ready.Count, held.Count, running.Count, budget.Bytes, SourcePayloadBudget.MaximumBytes);
            }
        }
    }

    /// <summary>元のSource セッションを、再開したセッションと混同せず受付に固定します。</summary>
    /// <returns>下流の完了を待たない受付結果です。</returns>
    internal ValueTask EnqueueAsync(Guid sessionId, NodeEmission emission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emission);
        cancellationToken.ThrowIfCancellationRequested();
        using (gate.EnterScope())
        {
            owners[emission] = sessionId;
            if (!emission.IsTerminal && cancelledSessions.Contains(sessionId))
            {
                Record(emission, "source.cancelledBeforeExecution", "元Sourceの明示取消後に到着した入力は開始しません。");
                return ValueTask.CompletedTask;
            }
            if (stopping.IsCancellationRequested)
            {
                Record(emission, "source.queueStopped", "入力受付は終了しています。");
                return ValueTask.CompletedTask;
            }
            if (HoldIfStopped(emission))
            {
                notifications.Wake();
                return ValueTask.CompletedTask;
            }
            if (!CanQueue(emission))
            {
                Record(emission, "source.queueCapacity", "実行待ちの保持上限に達した入力を未実行として拒否しました。");
                return ValueTask.CompletedTask;
            }
            if (Retain(emission))
            {
                ready.Add(emission);
                notifications.Wake();
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>停止した一件だけを専用領域へ保持します。終了通知は止めません。</summary>
    /// <returns>停止方針で処理したかを示します。</returns>
    private bool HoldIfStopped(NodeEmission emission)
    {
        if (emission.IsTerminal)
        {
            return false;
        }
        CompiledWorkflow? plan = emission.Context.CapturedWorkflow;
        NodeDefinition? node = plan?.FindNode(emission.Context.NodeId);
        if (node is null || !safety.IsStopped(plan!.Definition.Id, node))
        {
            return false;
        }
        EStoppedInputPolicy policy = node.Safety?.Inputs ?? EStoppedInputPolicy.Drop;
        if (policy == EStoppedInputPolicy.Drop)
        {
            Record(emission, "safety.dropped", "停止中の入力を設定された方針で破棄しました。");
            return true;
        }
        NodeEmission[] matching = held.Where(item => SameNode(item, emission)).ToArray();
        if (policy == EStoppedInputPolicy.Latest)
        {
            foreach (NodeEmission old in matching.Where(static item => CapturedPolicy(item) == EStoppedInputPolicy.Latest))
            {
                held.Remove(old);
                Record(old, "safety.replaced", "停止中に到着した新しい入力へ置き換えました。");
            }
        }
        if (held.Count < MaximumPending && held.Count(item => SameNode(item, emission)) < MaximumHeldPerNode)
        {
            if (Retain(emission))
            {
                held.Add(emission);
            }
        }
        else
        {
            Record(emission, "safety.capacity", "停止中入力の保持上限に達したため未実行として拒否しました。");
        }
        return true;
    }

    /// <summary>保持中入力を再確認し、停止したワークフローの後ろで他ワークフローを待たせません。</summary>
    /// <returns>受付終了までの処理です。</returns>
    private async Task ConsumeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await notifications.DrainAsync(running, cancellationToken).ConfigureAwait(false);
                bool hasHeld;
                using (gate.EnterScope())
                {
                    foreach (NodeEmission item in held.ToArray())
                    {
                        CompiledWorkflow plan = item.Context.CapturedWorkflow!;
                        NodeDefinition node = plan.OrderedNodes.Single(node => string.Equals(node.Id, item.Context.NodeId, StringComparison.Ordinal));
                        if (CanQueue(item) && !safety.IsStopped(plan.Definition.Id, node))
                        {
                            held.Remove(item);
                            ready.Add(item);
                        }
                    }
                    foreach (NodeEmission item in ready.ToArray())
                    {
                        (string Workflow, string Peer, bool Terminal) scope = (item.Context.WorkflowId, item.Context.PeerId, item.IsTerminal);
                        int limit = MaximumPending;
                        if (item.IsTerminal)
                        {
                            limit += MaximumRunningPerScope;
                        }
                        if (MaximumRunningPerScope <= active.GetValueOrDefault(scope) || limit <= running.Count)
                        {
                            continue;
                        }
                        ready.Remove(item);
                        if (running.ContainsKey(item.Context.CausationId))
                        {
                            Record(item, "source.duplicateCausation", "同じ入力識別子は既に実行中です。");
                            continue;
                        }
                        if (HoldIfStopped(item))
                        {
                            continue;
                        }
                        active.AddOrUpdate(scope, 1, static (_, value) => value + 1);
                        if (!running.TryAdd(item.Context.CausationId, ProcessAsync(item, cancellationToken)))
                        {
                            Record(item, "source.duplicateCausation", "同じ入力識別子は既に実行中です。");
                        }
                    }
                    hasHeld = held.Count != 0;
                }
                await notifications.WaitAsync(hasHeld, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 追跡済み処理はDisposeで終了を待ちます。
        }
    }

    /// <summary>安全停止を保持した実行枠ではなく、取得開始時のグラフで一件を進めます。</summary>
    /// <returns>下流一件の確認結果です。</returns>
    private async Task<EOutcome> ProcessAsync(NodeEmission emission, CancellationToken cancellationToken = default)
    {
        try
        {
            CompiledWorkflow plan = emission.Context.CapturedWorkflow ?? throw new InvalidOperationException("取得開始時の設定がありません。");
            ExecutionReport result = await engine.ExecuteEmissionAsync(plan, emission, cancellationToken).ConfigureAwait(false);
            return result.Outcome;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Record(emission, "source.itemUnconfirmed", "入力後の処理結果を確認できません。", EOutcome.Unknown);
            return EOutcome.Unknown;
        }
        finally
        {
            using (gate.EnterScope())
            {
                budget.Release(emission);
                engine.ScheduledTargets.Release(emission.Context.CausationId);
                owners.Remove(emission);
                (string Workflow, string Peer, bool Terminal) scope = (emission.Context.WorkflowId, emission.Context.PeerId, emission.IsTerminal);
                active.AddOrUpdate(scope, 0, static (_, value) => Math.Max(0, value - 1));
                active.TryRemove(new KeyValuePair<(string Workflow, string Peer, bool Terminal), int>(scope, 0));
                notifications.Complete(emission.Context.CausationId);
            }
        }
    }

    /// <summary>一件の非実行を有限の診断へ残します。</summary>
    private void Record(NodeEmission emission, string reason, string message, EOutcome outcome = EOutcome.NotExecuted)
    {
        using (gate.EnterScope())
        {
            budget.Release(emission);
            engine.ScheduledTargets.Release(emission.Context.CausationId);
            owners.Remove(emission);
        }
        failures[emission.Context.CausationId] = NodeResult.Failure(reason, message, outcome);
        if (emission.Context.Schedule is not null)
        {
            engine.ScheduledInputRejected?.Invoke(emission, failures[emission.Context.CausationId]);
        }
        foreach (Guid expired in failures.Keys.Skip(100))
        {
            failures.TryRemove(expired, out _);
        }
    }

    /// <summary>値のバイト上限を超えた入力を未実行として記録します。</summary>
    /// <returns>確保済みまたは新規に確保できたかを示します。</returns>
    private bool Retain(NodeEmission emission)
    {
        if (budget.TryRetain(emission))
        {
            if (!engine.ScheduledTargets.Track(emission))
            {
                Record(emission, "schedule.pendingCapacity", "時間指定の対象待ちの保持上限に達したため、入力を未実行として拒否しました。");
                return false;
            }
            return true;
        }
        Record(emission, "source.payloadCapacity", "入力値の保持byte上限に達したため未実行として拒否しました。");
        return false;
    }

    /// <summary>別ワークフロー・別Peer・別ノードの最新入力を混同しません。</summary>
    /// <returns>同じ入力保持範囲か。</returns>
    private static bool SameNode(NodeEmission first, NodeEmission second) => string.Equals(first.Context.WorkflowId, second.Context.WorkflowId, StringComparison.Ordinal) && string.Equals(first.Context.PeerId, second.Context.PeerId, StringComparison.Ordinal) && string.Equals(first.Context.NodeId, second.Context.NodeId, StringComparison.Ordinal);

    /// <summary>新設定から推測せず、その入力が取得した保持方針を読みます。</summary>
    /// <returns>取得時点の保持方針です。</returns>
    private static EStoppedInputPolicy CapturedPolicy(NodeEmission emission) => emission.Context.CapturedWorkflow?.FindNode(emission.Context.NodeId)?.Safety?.Inputs ?? EStoppedInputPolicy.Drop;

    /// <summary>取消配送中に新たな一件を始めず、待機入力を先に除きます。</summary>
    internal void MarkSessionCancelled(Guid sessionId)
    {
        using (gate.EnterScope())
        {
            cancelledSessions.Add(sessionId);
            foreach (NodeEmission emission in held.Concat(ready).Where(item => !item.IsTerminal && owners.GetValueOrDefault(item) == sessionId).ToArray())
            {
                held.Remove(emission);
                ready.Remove(emission);
                Record(emission, "source.cancelledBeforeExecution", "元Sourceの明示取消により待機入力を開始しません。");
            }
        }
    }

    /// <summary>元セッションの待機入力を未実行にし、進行中入力へ明示取消を伝えます。</summary>
    /// <returns>元の実行結果を確認できなければUnknownです。</returns>
    internal async Task<EOutcome> CancelSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        List<(Guid Id, Task<EOutcome> Task)> executions = [];
        using (gate.EnterScope())
        {
            cancelledSessions.Add(sessionId);
            foreach (NodeEmission emission in owners.Where(item => item.Value == sessionId && !item.Key.IsTerminal).Select(static item => item.Key).ToArray())
            {
                if (ready.Remove(emission) || held.Remove(emission))
                {
                    Record(emission, "source.cancelledBeforeExecution", "元Sourceの明示取消により待機入力を開始しません。");
                }
                else
                {
                    if (running.TryGetValue(emission.Context.CausationId, out Task<EOutcome>? task))
                    {
                        executions.Add((emission.Context.CausationId, task));
                    }
                }
            }
        }
        bool[] accepted = await Task.WhenAll(executions.Select(item => engine.RequestCancellationAsync(item.Id, cancellationToken))).ConfigureAwait(false);
        if (accepted.Any(static value => !value) && executions.Any(static item => !item.Task.IsCompleted))
        {
            return EOutcome.Unknown;
        }
        EOutcome[] outcomes = await Task.WhenAll(executions.Select(static item => item.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (outcomes.Contains(EOutcome.Unknown))
        {
            return EOutcome.Unknown;
        }
        return EOutcome.Cancelled;
    }

    /// <summary>実終了を確認したセッションの受付保護を解放します。</summary>
    internal void RetireSession(Guid sessionId)
    {
        using (gate.EnterScope())
        {
            cancelledSessions.Remove(sessionId);
        }
    }

    /// <summary>一ワークフローの滞留が全受付領域を使わないよう範囲別に制限します。</summary>
    /// <returns>通常入力と終了通知それぞれの保持余地です。</returns>
    private bool CanQueue(NodeEmission emission) => ready.Count(item => item.IsTerminal == emission.IsTerminal) < MaximumPending && ready.Count(item => item.IsTerminal == emission.IsTerminal && string.Equals(item.Context.WorkflowId, emission.Context.WorkflowId, StringComparison.Ordinal) && string.Equals(item.Context.PeerId, emission.Context.PeerId, StringComparison.Ordinal)) < MaximumHeldPerNode;

    /// <summary>受付を停止し、実行中の一件を追跡したまま終了します。</summary>
    /// <returns>所有する処理の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await consumer.Value.ConfigureAwait(false);
        using (gate.EnterScope())
        {
            foreach (NodeEmission emission in held.Concat(ready))
            {
                Record(emission, "source.queueStopped", "受付済み入力は実行開始前に終了しました。");
            }
            held.Clear();
            ready.Clear();
        }
        await Task.WhenAll(running.Values).ConfigureAwait(false);
        notifications.Dispose();
        stopping.Dispose();
    }
}
