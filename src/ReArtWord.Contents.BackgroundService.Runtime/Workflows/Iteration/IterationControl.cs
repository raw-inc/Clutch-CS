using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>一要素に届いた制御信号だけを保持し、開始済み処理を切り離さず新規開始を止めます。</summary>
internal sealed class IterationControl
{
    private readonly CompiledWorkflow workflow;
    private readonly Action breaking;
    private readonly IterationControl? parent;
    private string? requested;

    /// <summary>対応するEndと、外側の停止範囲を今回の要素へ固定します。</summary>
    internal IterationControl(CompiledWorkflow workflow, NodeDefinition? end, Action breaking, IterationControl? parent)
    {
        this.workflow = workflow;
        End = end;
        this.breaking = breaking;
        this.parent = parent;
        if (end?.Inputs is not null)
        {
            Set(IterationNodeTypes.RequestedControl(end.Inputs));
        }
    }

    /// <summary>この要素の結果を受ける終了ノードです。</summary>
    internal NodeDefinition? End
    {
        get;
    }

    /// <summary>この要素自身が受けた明示信号です。</summary>
    internal string? Requested => Volatile.Read(ref requested);

    /// <summary>外側で開始済みの入れ子も、新しい処理は開始しません。</summary>
    internal bool ParentClosed => parent?.IsClosed == true;

    /// <summary>まだ開始していない処理を省略するかです。</summary>
    internal bool IsClosed => Requested is not null || ParentClosed;

    /// <summary>同じ要素の実信号だけを読み、保持済みの値を新しい制御として再利用しません。</summary>
    internal void Observe(ExecutionFrame frame)
    {
        if (End is null || frame.ConfigurationOnly || frame.RetainedValue)
        {
            return;
        }
        WireDefinition[] wires = workflow.Incoming[End.Id].Where(wire => string.Equals(wire.FromNode, frame.NodeId, StringComparison.Ordinal)
            && IterationNodeTypes.IsControlPort(wire.ToPort)).ToArray();
        if (wires.Length != 0)
        {
            IReadOnlyDictionary<string, NodeValue> values = WireInputs.Create(End with { Inputs = null }, string.Empty, wires,
                new Dictionary<string, ExecutionFrame>(StringComparer.Ordinal) { [frame.NodeId] = frame }, null);
            Set(IterationNodeTypes.RequestedControl(values));
        }
    }

    /// <summary>制御信号が先着した場合は、まだ届かない結果値を待たず現在の入力だけを確定します。</summary>
    /// <returns>同じ要素で到着した親と、未到着の明示的な未実行結果です。</returns>
    internal async Task<Dictionary<string, ExecutionFrame>> EndInputsAsync(Task<Dictionary<string, ExecutionFrame>>[] parents,
        Dictionary<string, ExecutionFrame> inherited, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ExecutionFrame> available = new(inherited, StringComparer.Ordinal);
        List<Task<Dictionary<string, ExecutionFrame>>> pending = [.. parents];
        foreach (ExecutionFrame frame in inherited.Values)
        {
            Observe(frame);
        }
        while (pending.Count != 0)
        {
            foreach (Task<Dictionary<string, ExecutionFrame>> task in pending.Where(task => task.IsCompleted).ToArray())
            {
                pending.Remove(task);
                foreach ((string id, ExecutionFrame frame) in await task.ConfigureAwait(false))
                {
                    available[id] = frame;
                    Observe(frame);
                }
            }
            if (IsClosed || pending.Count == 0)
            {
                break;
            }
            try
            {
                await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Endは取消結果へ進み、開始済みの親処理はIterationScopeRunnerがすべて回収します。
                break;
            }
        }
        foreach (string id in workflow.Parents(End!.Id))
        {
            available.TryAdd(id, WorkflowEventDispatcher.Placeholder(id));
        }
        return available;
    }

    /// <summary>continue後に同じ要素でbreakも確定した場合は、未開始の要素を止めます。</summary>
    private void Set(string? control)
    {
        if (string.Equals(control, IterationNodeTypes.BreakPort, StringComparison.Ordinal))
        {
            string? previous = Interlocked.Exchange(ref requested, control);
            if (!string.Equals(previous, control, StringComparison.Ordinal))
            {
                breaking();
            }
            return;
        }
        if (control is not null)
        {
            Interlocked.CompareExchange(ref requested, control, null);
        }
    }
}
