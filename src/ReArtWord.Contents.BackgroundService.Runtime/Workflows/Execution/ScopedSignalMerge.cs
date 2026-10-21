using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>同じ要求の最初の有効信号を待ち、選択されない枝や別要素の値を補いません。</summary>
internal static class ScopedSignalMerge
{
    /// <summary>未完了の他入力を待たず、最初に届いた真の信号だけを採用します。</summary>
    /// <returns>確定した入力と、まだ届かない枝の未実行結果です。</returns>
    internal static async Task<ExecutionFrame[]> FirstAsync(CompiledWorkflow workflow, NodeDefinition node, Task<ExecutionFrame>[] parents, CancellationToken cancellationToken = default)
    {
        List<Task<ExecutionFrame>> pending = [.. parents];
        Dictionary<string, ExecutionFrame> frames = workflow.Incoming[node.Id].Select(wire => wire.FromNode).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, WorkflowEventDispatcher.Placeholder, StringComparer.Ordinal);
        while (pending.Count != 0)
        {
            Task<ExecutionFrame> ready = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            pending.Remove(ready);
            ExecutionFrame frame = await ready.ConfigureAwait(false);
            frames[frame.NodeId] = frame;
            if (frame.ConfigurationOnly || frame.RetainedValue)
            {
                continue;
            }
            WireDefinition[] wires = workflow.Incoming[node.Id].Where(wire => string.Equals(wire.FromNode, frame.NodeId, StringComparison.Ordinal) && 0 < SignalMergePorts.Index(wire.ToPort)).ToArray();
            IReadOnlyDictionary<string, NodeValue> values = WireInputs.Create(node with { Inputs = null }, string.Empty, wires, frames, null);
            if (values.Values.Any(value => value.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.True))
            {
                break;
            }
        }
        return frames.Values.ToArray();
    }

    /// <summary>反復を一単位として待つ親から、合流に接続された一つの出力を取得します。</summary>
    /// <returns>同じ階層の親結果です。</returns>
    internal static async Task<ExecutionFrame> FrameAsync(Task<Dictionary<string, ExecutionFrame>> parent, string id, CancellationToken cancellationToken = default)
        => (await parent.WaitAsync(cancellationToken).ConfigureAwait(false))[id];
}
