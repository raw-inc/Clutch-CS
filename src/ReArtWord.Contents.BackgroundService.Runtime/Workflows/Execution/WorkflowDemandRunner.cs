using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Retention;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>通常配送と同じ対象・取消境界を使い、要求時評価の結果も一件の報告へ含めます。</summary>
internal sealed class WorkflowDemandRunner(NodeRegistry registry, CompiledWorkflow workflow,
    IReadOnlyDictionary<string, TargetResolution> targets,
    Func<NodeDefinition, ExecutionFrame[], bool, IReadOnlyDictionary<string, NodeValue>?, bool, Guid, Task<ExecutionFrame>> execute, INodeStateStore? store,
    Func<ExecutionFrame, WorkflowEventContext, Task<ExecutionReport>> emit, NodeValueObservations observations, Guid causationId, string ownerPeer,
    NodeOutputRetentionQueue retentionQueue, WorkflowEventContext? context, WorkflowDebugCapture? debug, WorkflowProgressLifetime? progress,
    WorkflowConcurrencyExecution concurrency) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExecutionFrame> demanded = new(StringComparer.Ordinal);
    private readonly NodeOutputRetention retention = new(store, retentionQueue);
    private readonly WorkflowEvaluationCache evaluations = new();
    private readonly ConcurrentDictionary<Guid, Guid> progressAliases = new();
    private readonly WorkflowEventDispatcher signals = new(workflow, (node, frames, entry, inputs) => execute(node, frames, entry, inputs, false, context?.IterationScope ?? Guid.Empty), emit, debug);

    /// <summary>反復から発火した子も、元の要素範囲を維持します。</summary>
    /// <returns>内側の明示範囲、または継承した範囲です。</returns>
    private Guid ProgressScope(Guid scope)
    {
        if (progressAliases.TryGetValue(scope, out Guid inherited))
        {
            return inherited;
        }
        if (scope == Guid.Empty)
        {
            return context?.IterationScope ?? Guid.Empty;
        }
        return scope;
    }

    /// <summary>通常の親を待ち、開始可能な利用側にだけ遅延値を供給します。</summary>
    /// <returns>通常実行の結果です。</returns>
    internal async Task<ExecutionFrame> RunAsync(NodeDefinition node, Task<ExecutionFrame>[] parents, bool entry,
        IReadOnlyDictionary<string, NodeValue>? inputs, Func<string, Task<ExecutionFrame>?> read, Guid iterationScope = default, Func<bool>? stopped = null, CancellationToken cancellationToken = default)
    {
        if (ConcurrencyNodeTypes.IsStart(node.Type) && context?.ReadEnteredScope(workflow, node.Id, cancellationToken) is { } previous)
        {
            // 区間外のSignalが後から入る場合も、取得済みの入力を借りて制限区間そのものを通します。
            return await previous.ConfigureAwait(false);
        }
        if (ValueMergePorts.IsNode(node.Type) && workflow.ConfigurationValues?.ContainsKey(node.Id) == true)
        {
            return new ExecutionFrame(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("selection.configuration", "PC候補の合流は実行計画に反映済みです。", EOutcome.NotExecuted))]) { ConfigurationOnly = true };
        }
        string buttonType = "control.button";
        if (!entry && string.Equals(node.Type, buttonType, StringComparison.Ordinal))
        {
            return new(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("button.notPressed", "Press this button explicitly to start its downstream execution.", EOutcome.NotExecuted))]);
        }
        if (EventMergePorts.IsBoundary(node))
        {
            if (workflow.ScopedValueMerges.Contains(node.Id) && iterationScope != Guid.Empty)
            {
                ExecutionFrame merged = await ScopedValueMerge.ExecuteAsync(workflow, node, parents, iterationScope, (value, supplied, first, initial, readOnly) => execute(value, supplied, first, initial, readOnly, iterationScope), stopped, cancellationToken).ConfigureAwait(false);
                await signals.PublishAsync(merged, key => ReadEventValueAsync(key, read, cancellationToken), iterationScope, cancellationToken).ConfigureAwait(false);
                return merged;
            }
            if (ValueMergePorts.IsNode(node.Type) && iterationScope == Guid.Empty)
            {
                ExecutionFrame first = await signals.FirstValueAsync(node.Id, parents, cancellationToken).ConfigureAwait(false);
                await signals.PublishAsync(first, key => ReadEventValueAsync(key, read, cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
                return first;
            }
            return WorkflowEventDispatcher.Placeholder(node.Id);
        }
        ExecutionFrame[] frames;
        if (SignalMergePorts.IsScopedFirst(node))
        {
            frames = await ScopedSignalMerge.FirstAsync(workflow, node, parents, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            frames = await Task.WhenAll(parents).ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node);
        }
        if (debug is null && !entry && workflow.ConfigurationValues?.ContainsKey(node.Id) == true && frames.All(frame => frame.ConfigurationOnly))
        {
            bool pc = registry.Find(node.Type)!.Descriptor.Outputs.Any(port => port.ValueKind == EPortValueKind.PcSelection);
            string reason = pc ? "selection.configuration"
                               : "value.configuration";
            string message = pc ? "PC選択の条件は実行計画に反映済みです。"
                                : "この一件では実行せず、設定版の純粋値だけを供給します。";
            return new(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure(reason, message, EOutcome.NotExecuted))], ConfigurationOnly: true);
        }
        if (IterationNodeTypes.IsEnd(node.Type) && IterationNodeTypes.RequestedControl(WireInputs.Create(node, string.Empty, workflow.Incoming[node.Id],
            frames.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal), inputs)) is not null)
        {
            return await execute(node, frames, entry, inputs, false, ProgressScope(iterationScope)).ConfigureAwait(false);
        }
        if (targets[node.Id].Error is null && targets[node.Id].Peers.Count != 0)
        {
            RuntimeDemandValues values = CreateValues(read, stopped, cancellationToken);
            try
            {
                frames = await values.SupplyAsync(node, frames, entry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(node);
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node);
        }
        string trigger = "trigger";
        bool boundary = WorkflowRegionNodeTypes.IsStart(node.Type) || WorkflowRegionNodeTypes.IsEnd(node.Type);
        Dictionary<string, ExecutionFrame> suppliedFrames = frames.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        bool retains = !boundary && !workflow.PcSelectionValues.Contains(node.Id) && registry.Find(node.Type) is IActionNode && workflow.Incoming[node.Id].Any(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal))
            && PureValueInputs.CheckActivation(registry, node, workflow, suppliedFrames) is null;
        ExecutionFrame completed;
        if (retains)
        {
            string key = ContractJson.Hash(new { node.Id, Iteration = iterationScope, Signals = workflow.Incoming[node.Id].Where(wire => string.Equals(wire.ToPort, trigger, StringComparison.Ordinal))
                .Select(wire => new { wire.FromNode, wire.FromPort, suppliedFrames[wire.FromNode].SignalId }) });
            Lazy<Task<ExecutionFrame>> proposed = new(() => ExecuteSignaledAsync(node, frames, entry, inputs, iterationScope, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<ExecutionFrame>>? accepted = evaluations.GetOrAdd(iterationScope, key, true, proposed);
            if (accepted is null)
            {
                return new(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("signal.executionLimit", "The execution's signal limit was reached; this operation was not started.", EOutcome.NotExecuted))]);
            }
            completed = await accepted.Value.ConfigureAwait(false);
            if (!ReferenceEquals(accepted, proposed))
            {
                completed = completed with { ConfigurationOnly = true, RetainedValue = true };
            }
        }
        else
        {
            completed = await ExecuteValueAsync(node, frames, entry, inputs, false, iterationScope, cancellationToken).ConfigureAwait(false);
        }
        if (!boundary && stopped?.Invoke() != true)
        {
            await signals.PublishAsync(completed, key => ReadEventValueAsync(key, read, cancellationToken), iterationScope, cancellationToken).ConfigureAwait(false);
        }
        return completed;
    }

    /// <summary>通常の値要求と子実行の値参照で、元の要求の評価キャッシュを共有します。</summary>
    /// <returns>今回の範囲だけを遡る値評価です。</returns>
    private RuntimeDemandValues CreateValues(Func<string, Task<ExecutionFrame>?> read, Func<bool>? stopped, CancellationToken cancellationToken = default)
    {
        return new(registry, workflow, read, async (dependency, supplied, branchRead) =>
        {
            Guid scope = await RecipeIterationInputs.ScopeAsync(workflow, dependency.Id, read, cancellationToken).ConfigureAwait(false);
            ExecutionFrame result = await ExecuteValueAsync(dependency, supplied, true, null, branchRead || dependency.Type is "control.toggleButton", scope, cancellationToken).ConfigureAwait(false);
            if (branchRead && ValueMergePorts.IsNode(dependency.Type))
            {
                result = result with { ConfigurationOnly = true };
            }
            demanded.AddOrUpdate(result.NodeId, result, (_, old) => Combine(old, result));
            if (!branchRead && stopped?.Invoke() != true)
            {
                await signals.PublishAsync(result, key => ReadEventValueAsync(key, read, cancellationToken), scope, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }, retention, key => context?.Read(key, cancellationToken));
    }

    /// <summary>未評価の値も親の要求で一度だけ評価し、同じ要求で確定した失敗を過去値へ置き換えません。</summary>
    /// <returns>子実行へ供給する現在の値です。</returns>
    private async Task<ExecutionFrame> ReadEventValueAsync(string id, Func<string, Task<ExecutionFrame>?> read, CancellationToken cancellationToken = default)
        => await CreateValues(read, null, cancellationToken).ResolveAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false) ?? WorkflowEventDispatcher.Placeholder(id);

    /// <summary>入力待ちで取り消されたノードを、入力不備や副作用の失敗に読み替えません。</summary>
    /// <returns>未開始を明示した取消結果です。</returns>
    private static ExecutionFrame Cancelled(NodeDefinition node) => new(node.Id, EOutcome.Cancelled,
        [new(string.Empty, NodeResult.Failure("execution.cancelledBeforeStart", "The execution was cancelled before this node started.", EOutcome.Cancelled))]);

    /// <summary>一つの開始信号を一度だけ実行し、更新世代と確定結果を保存します。</summary>
    /// <returns>同じ信号を再参照しても再実行しない結果です。</returns>
    private async Task<ExecutionFrame> ExecuteSignaledAsync(NodeDefinition node, ExecutionFrame[] frames, bool entry,
        IReadOnlyDictionary<string, NodeValue>? inputs, Guid iterationScope, CancellationToken cancellationToken = default)
    {
        NodeOutputRetention.Generation? generation;
        try
        {
            generation = await retention.BeginAsync(workflow, node.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            generation = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node);
        }
        if (generation is null)
        {
            return new(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("value.retentionUnavailable", "The signal generation could not be stored; this node was not started.", EOutcome.NotExecuted))]);
        }
        ExecutionFrame completed = await execute(node, frames, entry, inputs, false, ProgressScope(iterationScope)).ConfigureAwait(false);
        bool saved;
        try
        {
            saved = await retention.CompleteAsync(workflow, node.Id, generation, completed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            saved = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            saved = false;
        }
        if (!saved)
        {
            completed = completed with { Targets = completed.Targets.Select(target => target with { Result = target.Result with
            {
                ReasonCode = "value.retentionUnconfirmed", Message = "The operation result is known, but retention for later reads could not be confirmed. The operation was not repeated.",
            } }).ToArray() };
        }
        return evaluations.Retain(iterationScope, completed);
    }

    /// <summary>同じ一件の同一入力を共有し、反復だけで再実行せず入力の変化を識別します。</summary>
    /// <returns>同じ値要求が合流する一回の実行です。</returns>
    private Task<ExecutionFrame> ExecuteValueAsync(NodeDefinition node, ExecutionFrame[] frames, bool entry,
        IReadOnlyDictionary<string, NodeValue>? initial, bool valueRead, Guid iterationScope = default, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ExecutionFrame> supplied = frames.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        bool activated = entry || PureValueInputs.CheckActivation(registry, node, workflow, supplied) is null;
        if (!activated || registry.Find(node.Type) is not IActionNode || WorkflowRegionNodeTypes.IsStart(node.Type) || WorkflowRegionNodeTypes.IsEnd(node.Type))
        {
            return execute(node, frames, entry, initial, valueRead, ProgressScope(iterationScope));
        }
        IReadOnlyDictionary<string, NodeValue> inputs = PureValueInputs.Create(registry, node, string.Empty, workflow, supplied, initial,
            ignoreTrigger: node.Type is "control.toggleButton" && (valueRead || entry)).Inputs;
        NodeDescriptor descriptor = registry.Find(node.Type)!.Descriptor;
        bool signalGenerator = StartSignalPorts.IsOutput(descriptor, StartSignalPorts.Output)
            || descriptor.Outputs.Any(port => !CompletionPorts.IsPort(port.Name) && StartSignalPorts.IsOutput(descriptor, port.Name));
        Guid[]? arrivals = null;
        if (signalGenerator)
        {
            arrivals = frames.Select(frame => frame.SignalId).ToArray();
        }
        string key = ContractJson.Hash(new { node.Id, node.Settings, Inputs = inputs, Arrivals = arrivals, Iteration = iterationScope, ValueRead = valueRead });
        Lazy<Task<ExecutionFrame>> proposed = new(() => EvaluateSharedAsync(node, frames, entry, initial, valueRead, iterationScope, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<ExecutionFrame>>? cached = evaluations.GetOrAdd(iterationScope, key, false, proposed);
        if (cached is null)
        {
            return Task.FromResult(new ExecutionFrame(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("value.shareLimit", "The execution's shared-value limit was reached. This operation was not started.", EOutcome.NotExecuted))]));
        }
        return cached.Value;
    }

    /// <summary>同じ一件の出力保持量を有限にし、保持できない値を捨てて副作用を再実行しません。</summary>
    /// <returns>確認済みの実成否と、保持可能な場合の値です。</returns>
    private async Task<ExecutionFrame> EvaluateSharedAsync(NodeDefinition node, ExecutionFrame[] frames, bool entry,
        IReadOnlyDictionary<string, NodeValue>? initial, bool valueRead, Guid iterationScope, CancellationToken cancellationToken = default)
    {
        ExecutionFrame result = await execute(node, frames, entry, initial, valueRead, ProgressScope(iterationScope)).ConfigureAwait(false);
        return evaluations.Retain(iterationScope, result);
    }

    /// <summary>スクリプトの失敗を、利用側の未実行だけの報告に隠しません。</summary>
    /// <returns>要求時評価の実成否を含めたノード結果です。</returns>
    private ExecutionFrame[] IncludeResults(ExecutionFrame[] frames) => frames.Select(frame => demanded.TryGetValue(frame.NodeId, out ExecutionFrame? value) ? Combine(frame, value)
                                                                                                                                                             : frame).ToArray();

    /// <summary>通常グラフと反復内の評価範囲を分け、開始済み依存だけを共有します。</summary>
    /// <returns>一件の要求時評価も含む有限な結果です。</returns>
    internal async Task<ExecutionFrame[]> ExecuteAsync(HashSet<string> entries, IReadOnlyDictionary<string, NodeValue>? inputs,
        ExecutionFrame? seed, Func<bool> cancelled, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        signals.IterationLifetime = evaluations.Child;
        signals.ProgressScope = ProgressScope;
        evaluations.Released = id =>
        {
            if (!progressAliases.TryRemove(id, out _))
            {
                progress?.CompleteScope(id);
            }
        };
        ExecutionFrame[] frames;
        if (workflow.Iterations?.Regions.Count is > 0)
        {
            IterationRunner runner = new(workflow,
                (node, parents, entry, initial, read, iterationScope, stopped) => RunAsync(node, parents, entry, initial,
                    read, iterationScope, stopped, cancellationToken), cancelled, clock,
                (frame, read) => signals.PublishAsync(frame, key => ReadEventValueAsync(key, read, cancellationToken), frame.ElementStart ? frame.SignalId
                                                                                                                                          : Guid.Empty, cancellationToken), observations, causationId, ownerPeer, concurrency, evaluations.CompleteAndWaitAsync, (scope, parent) => progressAliases[scope] = ProgressScope(parent), evaluations.Complete);
            frames = await runner.RunAsync(entries, inputs, seed, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Dictionary<string, Lazy<Task<ExecutionFrame>>> tasks = new(StringComparer.Ordinal);

            async Task<ExecutionFrame> StartAsync(NodeDefinition node)
            {
                // 公開前に全ノードの参照口を確定し、同期完了するノードからの子実行も未開始の兄弟を待てるようにします。
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                if (seed is not null && string.Equals(node.Id, seed.NodeId, StringComparison.Ordinal))
                {
                    return seed;
                }
                Task<ExecutionFrame>[] parents = workflow.Parents(node.Id).Select(parent => tasks[parent].Value).ToArray();
                return await RunAsync(node, parents, entries.Contains(node.Id), inputs, key => tasks.GetValueOrDefault(key)?.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            foreach (NodeDefinition node in workflow.OrderedNodes)
            {
                tasks.Add(node.Id, new(() => StartAsync(node), LazyThreadSafetyMode.ExecutionAndPublication));
            }
            if (seed is not null)
            {
                await signals.PublishAsync(seed, key => ReadEventValueAsync(key, item => tasks.GetValueOrDefault(item)?.Value, cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            frames = await Task.WhenAll(workflow.OrderedNodes.Select(node => tasks[node.Id].Value)).ConfigureAwait(false);
        }
        IReadOnlyDictionary<string, ExecutionFrame> merged = await signals.FinishAsync(CancellationToken.None).ConfigureAwait(false);
        return IncludeResults(frames.Select(frame => merged.GetValueOrDefault(frame.NodeId, frame)).ToArray());
    }

    /// <summary>複数回の値を単一結果に見せず、対象ごとの成否だけを有限に保持します。</summary>
    /// <returns>確認済み実行を優先した集約結果です。</returns>
    private static ExecutionFrame Combine(ExecutionFrame earlier, ExecutionFrame current)
    {
        if (ReferenceEquals(earlier, current) || earlier.Outcome == EOutcome.NotExecuted)
        {
            return current;
        }
        EOutcome outcome = ExecutionOutcomes.Aggregate([earlier.Outcome, current.Outcome]);
        TargetNodeResult[] targets = earlier.Targets.Concat(current.Targets).GroupBy(target => target.PeerId, StringComparer.Ordinal).Select(group =>
        {
            EOutcome combined = ExecutionOutcomes.Aggregate(group.Select(target => target.Result.Outcome));
            TargetNodeResult representative = group.First(target => target.Result.Outcome == combined);
            return representative with { Result = representative.Result with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) } };
        }).ToArray();
        return current with { Outcome = outcome, Targets = targets };
    }

    /// <summary>通常グラフの失敗・取消でも、開始した信号の後続を置き去りにしません。</summary>
    /// <returns>子実行の終了と保持枠の破棄です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名であり、既に開始した処理の終了待ちは取消しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの契約を実装します。子実行は所有元の実行取消に従い、破棄では終了を必ずawaitします。")]
    public async ValueTask DisposeAsync()
    {
        try
        {
            await signals.FinishAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            signals.Dispose();
            evaluations.Clear();
        }
    }
}
