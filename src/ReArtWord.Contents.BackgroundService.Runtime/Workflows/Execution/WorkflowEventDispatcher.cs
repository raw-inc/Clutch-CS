using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>信号と値の合流を全入力待ちから分離し、一件に固定した後続を追跡します。</summary>
internal sealed class WorkflowEventDispatcher(CompiledWorkflow workflow,
    Func<NodeDefinition, ExecutionFrame[], bool, IReadOnlyDictionary<string, NodeValue>?, Task<ExecutionFrame>> execute,
    Func<ExecutionFrame, WorkflowEventContext, Task<ExecutionReport>> emit, WorkflowDebugCapture? debug) : IDisposable
{
    /// <summary>反復から発火した子処理の開始と終了を、値の所有範囲へ通知します。</summary>
    internal Action<Guid, bool, EOutcome>? IterationLifetime
    {
        get; set;
    }

    /// <summary>制限区間は親の反復表示を継承し、Progressだけの新しい反復を作りません。</summary>
    internal Func<Guid, Guid>? ProgressScope
    {
        get; set;
    }

    private readonly ILookup<string, WireDefinition> destinations = workflow.Incoming.Values.SelectMany(wires => wires)
        .Where(wire => EventMergePorts.IsBoundary(workflow.FindNode(wire.ToNode)!) && !workflow.ScopedValueMerges.Contains(wire.ToNode)
            && workflow.ConfigurationValues?.ContainsKey(wire.ToNode) != true && 0 < ConcatPorts.Index(wire.ToPort)).ToLookup(wire => wire.FromNode, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> published = new();
    private readonly ConcurrentDictionary<string, ExecutionFrame> results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecutionFrame>> firstValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> children = new();
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> descendants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim capacity = new(16);
    private int accepted;

    /// <summary>実際の完了・受信一件を、接続された合流口へ配送します。</summary>
    /// <returns>状態更新と後続の開始を追跡できる段階です。</returns>
    internal async Task PublishAsync(ExecutionFrame frame, Func<string, Task<ExecutionFrame>?> read, Guid iterationScope = default, CancellationToken cancellationToken = default)
    {
        if (!destinations.Contains(frame.NodeId) || frame.ConfigurationOnly || frame.RetainedValue || frame.SignalId == Guid.Empty || !published.TryAdd(frame.SignalId, 0))
        {
            return;
        }
        foreach (WireDefinition wire in destinations[frame.NodeId])
        {
            if (!frame.ElementStart && ConcurrencyNodeTypes.IsStart(workflow.FindNode(frame.NodeId)!.Type)
                && WorkflowRegionNodeTypes.IsBodyPort(ConcurrencyNodeTypes.Start, wire.FromPort))
            {
                continue;
            }
            NodeDefinition node = workflow.FindNode(wire.ToNode)!;
            foreach ((Guid Id, NodeValue Value, ExecutionFrame Origin) input in ReadInputs(node, wire, frame))
            {
                await ReceiveAsync(node, wire, input, read, iterationScope == Guid.Empty && !frame.ElementStart, iterationScope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>PC別の値を配列へ混ぜず、配線で選択された値を一件ずつ固定します。</summary>
    /// <returns>既存の信号識別またはPC別の値事象と、その所有済み入力です。</returns>
    private IEnumerable<(Guid Id, NodeValue Value, ExecutionFrame Origin)> ReadInputs(NodeDefinition node, WireDefinition wire, ExecutionFrame frame)
    {
        bool values = ValueMergePorts.IsNode(node.Type);
        if (values && (frame.SourceActivation || frame.SourceCompletion || frame.Outcome == EOutcome.NotExecuted))
        {
            yield break;
        }
        IEnumerable<ExecutionFrame> events = [frame];
        if (values)
        {
            events = frame.Targets.Where(target => target.Result.Outcome != EOutcome.NotExecuted)
                .Select(target => frame with { Outcome = target.Result.Outcome, Targets = [target] });
        }
        foreach (ExecutionFrame item in events)
        {
            IReadOnlyDictionary<string, NodeValue> selected;
            try
            {
                selected = WireInputs.Create(node with { Inputs = null }, string.Empty, [wire],
                    new Dictionary<string, ExecutionFrame>(StringComparer.Ordinal) { [item.NodeId] = item }, null);
            }
            catch (FormatException)
            {
                ExecutionFrame rejected = new(node.Id, EOutcome.Failed, [new(string.Empty,
                    NodeResult.Failure("input.path", "入力の型と抽出パスが一致しません。", EOutcome.NotExecuted))]);
                results.AddOrUpdate(node.Id, rejected, (_, previous) => Summary(previous, rejected));
                continue;
            }
            if (!selected.TryGetValue(wire.ToPort, out NodeValue? value)
                || !values && (value.Kind != EValueKind.Json || value.Json.ValueKind != System.Text.Json.JsonValueKind.True))
            {
                continue;
            }
            string identity = ContractJson.Hash(new { frame.SignalId, wire.ToNode, wire.ToPort });
            if (values)
            {
                identity = ContractJson.Hash(new { frame.SignalId, wire.ToNode, wire.ToPort, item.Targets[0].PeerId });
            }
            yield return (Guid.ParseExact(identity[..32], "N"), value, item);
        }
    }

    /// <summary>最初の値は同じ一件へ渡し、追加の到着と反復の各要素は独立した後続へ固定します。</summary>
    /// <returns>後続処理を所有する集合へ登録するまでの受付です。</returns>
    // 抑制理由: 子実行は集合に保持し、通常完了と所有元WorkflowDemandRunnerの破棄がFinishAsyncを通して必ず終了を待ちます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "子実行を追跡し、完了と破棄の両経路で集合全体をawaitします。")]
    private async Task ReceiveAsync(NodeDefinition node, WireDefinition wire, (Guid Id, NodeValue Value, ExecutionFrame Origin) input,
        Func<string, Task<ExecutionFrame>?> read, bool inlineValue, Guid iterationScope, CancellationToken cancellationToken = default)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { [wire.ToPort] = input.Value, ["$signalEvent"] = NodeValue.FromObject(input.Id) };
        ExecutionFrame[] parents = workflow.Incoming[node.Id].Select(item => item.FromNode).Distinct(StringComparer.Ordinal).Select(Placeholder).ToArray();
        ExecutionFrame received = (await execute(node, parents, true, inputs).ConfigureAwait(false)) with { SignalId = input.Id };
        if (received.Outcome == EOutcome.NotExecuted)
        {
            bool cancelled = received.Targets.Count != 0 && received.Targets.All(target => target.Result.ReasonCode is "execution.cancelledBeforeStart");
            EOutcome outcome = EOutcome.Failed;
            if (cancelled)
            {
                outcome = EOutcome.Cancelled;
            }
            received = received with { Outcome = outcome };
        }
        results.AddOrUpdate(node.Id, received, (_, previous) => Summary(previous, received));
        if (inlineValue && ValueMergePorts.IsNode(node.Type) && FirstValue(node.Id).TrySetResult(received))
        {
            return;
        }
        bool fires = received.Targets.Any(target => target.Result.Outputs.TryGetValue(StartSignalPorts.Output, out NodeValue? signal) && signal.Kind == EValueKind.Json && signal.Json.ValueKind == System.Text.Json.JsonValueKind.True);
        if (!fires)
        {
            return;
        }
        if (16384 < Interlocked.Increment(ref accepted))
        {
            ExecutionFrame stopped = new(node.Id, EOutcome.Failed, [new(string.Empty, NodeResult.Failure("signal.executionLimit", "The execution's event limit was reached; this downstream operation was not started.", EOutcome.NotExecuted))]);
            results.AddOrUpdate(node.Id, stopped, (_, previous) => Summary(previous, stopped));
            IterationLifetime?.Invoke(iterationScope, true, EOutcome.Succeeded);
            IterationLifetime?.Invoke(iterationScope, false, EOutcome.Failed);
            return;
        }
        WorkflowEventContext context = new(descendants.GetOrAdd(node.Id, id => WorkflowEventContext.Descendants(workflow, id)), input.Origin, read, debug, cancellationToken) { IterationScope = ProgressScope?.Invoke(iterationScope) ?? iterationScope };
        Lazy<Task> child = new(() => RunChildAsync(received, context, iterationScope, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
        IterationLifetime?.Invoke(iterationScope, true, EOutcome.Succeeded);
        children[input.Id] = child;
        _ = child.Value;
    }

    /// <summary>最初の値を同じ実行へ渡し、別経路のSignalや値と合流できるようにします。</summary>
    /// <returns>他の入力完了を待たない一件の値、または一件も届かなかった未実行結果です。</returns>
    // 抑制理由: 完了通知と親タスクは実行全体の所有物であり、先着待ちの終了では破棄しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "完了通知と親タスクを借用して待機します。実行全体が所有するタスクをこのメソッドでは破棄しません。")]
    internal async Task<ExecutionFrame> FirstValueAsync(string node, Task<ExecutionFrame>[] parents, CancellationToken cancellationToken = default)
    {
        Task<ExecutionFrame> first = FirstValue(node).Task;
        List<Task<ExecutionFrame>> pending = [first, .. parents];
        try
        {
            while (1 < pending.Count)
            {
                Task<ExecutionFrame> completed = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(completed, first))
                {
                    return await first.ConfigureAwait(false);
                }
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(node, EOutcome.Cancelled, [new(string.Empty, NodeResult.Failure("execution.cancelledBeforeStart", "The execution was cancelled before a value arrived.", EOutcome.Cancelled))]);
        }
        if (first.IsCompleted)
        {
            return await first.ConfigureAwait(false);
        }
        return BranchInputSelection.ClosedMerge(workflow, node, await Task.WhenAll(parents).ConfigureAwait(false)) ?? Placeholder(node);
    }

    /// <summary>後続が待機を開始するより早い到着も、一度だけ確定します。</summary>
    /// <returns>同じ一件の最初の値を所有する完了通知です。</returns>
    private TaskCompletionSource<ExecutionFrame> FirstValue(string node) => firstValues.GetOrAdd(node, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>受信済みのすべての後続を待ち、保存結果不明からの再送を行いません。</summary>
    /// <returns>元の一件に含める合流の実成否です。</returns>
    internal async Task<IReadOnlyDictionary<string, ExecutionFrame>> FinishAsync(CancellationToken cancellationToken = default)
    {
        while (!children.IsEmpty)
        {
            await Task.WhenAll(children.Values.Select(child => child.Value)).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>同じ要求の値を借用して後続を開始し、供給側の完了を並列枠待ちで阻みません。</summary>
    /// <returns>確認済みの子実行の終了です。</returns>
    // 抑制理由: 所有する子実行の例外を結果不明として記録し、破棄後に未観測Taskを残しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "ワークフロー実行境界で確認不能を記録し、処理を再送しません。")]
    private async Task RunChildAsync(ExecutionFrame signal, WorkflowEventContext context, Guid iterationScope, CancellationToken cancellationToken = default)
    {
        bool acquired = false;
        EOutcome outcome = EOutcome.Unknown;
        try
        {
            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            ExecutionReport report = await emit(signal, context).ConfigureAwait(false);
            outcome = report.Outcome;
            if (report.Outcome is EOutcome.Failed or EOutcome.Unknown or EOutcome.Cancelled)
            {
                ExecutionFrame failed = new(signal.NodeId, report.Outcome, [new(string.Empty, NodeResult.Failure("signal.downstream", "A started downstream execution did not succeed.", report.Outcome))]);
                results.AddOrUpdate(signal.NodeId, failed, (_, previous) => Summary(previous, failed));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = EOutcome.Cancelled;
            ExecutionFrame stopped = new(signal.NodeId, EOutcome.Cancelled, [new(string.Empty, NodeResult.Failure("signal.outputCancelled", "The downstream execution was cancelled before dispatch.", EOutcome.Cancelled))]);
            results.AddOrUpdate(signal.NodeId, stopped, (_, previous) => Summary(previous, stopped));
        }
        catch (Exception)
        {
            ExecutionFrame unknown = new(signal.NodeId, EOutcome.Unknown, [new(string.Empty, NodeResult.Failure("signal.downstreamUnconfirmed", "The started downstream execution could not be confirmed.", EOutcome.Unknown))]);
            results.AddOrUpdate(signal.NodeId, unknown, (_, previous) => Summary(previous, unknown));
        }
        finally
        {
            if (acquired)
            {
                capacity.Release();
            }
            IterationLifetime?.Invoke(iterationScope, false, outcome);
            children.TryRemove(signal.SignalId, out _);
        }
    }

    /// <summary>別実行が担当する合流や未到着の入力から、後続を二重に起動しません。</summary>
    /// <returns>信号事象側が所有するノードの未実行フレームです。</returns>
    internal static ExecutionFrame Placeholder(string id) => new(id, EOutcome.NotExecuted,
        [new(string.Empty, NodeResult.Failure("signal.eventBoundary", "This node is driven by signal events.", EOutcome.NotExecuted))], AwaitingEvent: true);

    /// <summary>一件内の複数の発火結果を、全出力を保持せず集約します。</summary>
    /// <returns>合流ノードの実成否です。</returns>
    private static ExecutionFrame Summary(ExecutionFrame earlier, ExecutionFrame current)
    {
        EOutcome outcome = ExecutionOutcomes.Aggregate([earlier.Outcome, current.Outcome]);
        ExecutionFrame representative = current.Outcome == outcome ? current
                                                                   : earlier;
        return representative with { Outcome = outcome, Targets = representative.Targets.Select(target => target with
        {
            Result = target.Result with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) },
        }).ToArray() };
    }

    /// <summary>開始済みの後続を待ち終わってから保持枠を解放します。</summary>
    public void Dispose() => capacity.Dispose();
}
