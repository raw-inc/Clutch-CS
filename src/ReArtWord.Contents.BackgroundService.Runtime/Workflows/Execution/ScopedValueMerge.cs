using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>一つの反復要素の先着値を固定し、別要素の到着状態を共有しません。</summary>
internal static class ScopedValueMerge
{
    /// <summary>要素内の一つの到着だけを配送し、後着や別反復から同じ後続を起動しません。</summary>
    /// <returns>この要素が所有する値合流の結果です。</returns>
    internal static async Task<ExecutionFrame> ExecuteAsync(CompiledWorkflow workflow, NodeDefinition node, Task<ExecutionFrame>[] parents,
        Guid iterationScope, Func<NodeDefinition, ExecutionFrame[], bool, IReadOnlyDictionary<string, NodeValue>?, bool, Task<ExecutionFrame>> execute,
        Func<bool>? stopped, CancellationToken cancellationToken = default)
    {
        (Guid Id, IReadOnlyDictionary<string, NodeValue> Inputs)? arrival;
        try
        {
            arrival = await FirstAsync(workflow, node, parents, iterationScope, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return new(node.Id, EOutcome.Failed, [new(string.Empty, NodeResult.Failure("input.path", "The arriving value does not match its extraction path."))]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(node);
        }
        if (stopped?.Invoke() == true || arrival is null)
        {
            if (stopped?.Invoke() != true && BranchInputSelection.ClosedMerge(workflow, node.Id, await Task.WhenAll(parents).ConfigureAwait(false)) is { } excluded)
            {
                return excluded;
            }
            return new(node.Id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("input.inactive", "No value arrived before this iteration element completed.", EOutcome.NotExecuted))]);
        }
        ExecutionFrame[] frames = workflow.Parents(node.Id).Select(WorkflowEventDispatcher.Placeholder).ToArray();
        return (await execute(node, frames, true, arrival.Value.Inputs, false).ConfigureAwait(false)) with { SignalId = arrival.Value.Id };
    }

    /// <summary>未選択の枝と過去値を除き、届いた一対象・一接続の値をそのまま取得します。</summary>
    /// <returns>反復要素に固定した到着、または全入力が未供給ならnullです。</returns>
    private static async Task<(Guid Id, IReadOnlyDictionary<string, NodeValue> Inputs)?> FirstAsync(CompiledWorkflow workflow, NodeDefinition node,
        Task<ExecutionFrame>[] parents, Guid iterationScope, CancellationToken cancellationToken = default)
    {
        List<Task<ExecutionFrame>> pending = [.. parents];
        while (pending.Count != 0)
        {
            Task<ExecutionFrame> ready = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            pending.Remove(ready);
            ExecutionFrame frame = await ready.ConfigureAwait(false);
            if (frame.ConfigurationOnly || frame.RetainedValue || frame.SourceActivation || frame.SourceCompletion || frame.Outcome == EOutcome.NotExecuted)
            {
                continue;
            }
            foreach (WireDefinition wire in workflow.Incoming[node.Id].Where(wire => string.Equals(wire.FromNode, frame.NodeId, StringComparison.Ordinal) && 0 < ConcatPorts.Index(wire.ToPort)))
            {
                foreach (TargetNodeResult target in frame.Targets.Where(target => target.Result.Outcome != EOutcome.NotExecuted))
                {
                    Dictionary<string, ExecutionFrame> supplied = new(StringComparer.Ordinal) { [frame.NodeId] = frame with { Targets = [target] } };
                    IReadOnlyDictionary<string, NodeValue> values = WireInputs.Create(node with { Inputs = null }, string.Empty, [wire], supplied, null);
                    if (!values.TryGetValue(wire.ToPort, out NodeValue? value))
                    {
                        continue;
                    }
                    Guid id = Guid.ParseExact(ContractJson.Hash(new { iterationScope, frame.SignalId, node.Id, wire.ToPort, target.PeerId })[..32], "N");
                    return (id, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { [wire.ToPort] = value, ["$signalEvent"] = NodeValue.FromObject(id) });
                }
            }
        }
        return null;
    }

    /// <summary>取消済みの先着待ちを、実行結果不明や新しい到着にしません。</summary>
    /// <returns>未開始の合流の取消結果です。</returns>
    private static ExecutionFrame Cancelled(NodeDefinition node) => new(node.Id, EOutcome.Cancelled,
        [new(string.Empty, NodeResult.Failure("execution.cancelledBeforeStart", "The execution was cancelled before dispatch.", EOutcome.Cancelled))]);
}
