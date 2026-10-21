using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>合流を発火した要求の値だけを借用し、子側の実行範囲や別要素の結果を取り込みません。</summary>
internal sealed class WorkflowEventContext
{
    /// <summary>反復から発火した子が引き継ぐ要素範囲です。</summary>
    internal Guid IterationScope
    {
        get; init;
    }

    private readonly IReadOnlySet<string> downstream;
    private readonly ExecutionFrame origin;
    private readonly Func<string, Task<ExecutionFrame>?> read;

    /// <summary>発火時の要素範囲と発火元を固定し、まだ完了していない同じ要求の入力も参照可能にします。</summary>
    internal WorkflowEventContext(IReadOnlySet<string> downstream, ExecutionFrame origin, Func<string, Task<ExecutionFrame>?> read, WorkflowDebugCapture? debug, CancellationToken cancellationToken = default)
    {
        this.downstream = downstream;
        this.origin = origin;
        this.read = read;
        Cancellation = cancellationToken;
        Debug = debug;
    }

    /// <summary>遠隔取消の配送完了より先に、親と同時に子の入力待ち・未開始処理を止めます。</summary>
    internal CancellationToken Cancellation
    {
        get;
    }

    /// <summary>合流後も元の一回だけに記録をまとめます。</summary>
    internal WorkflowDebugCapture? Debug
    {
        get;
    }

    /// <summary>区間入口を通らない別経路の事象が、区間内へ到着したかを判定します。</summary>
    /// <returns>区間外からの到着で借用する入口の結果です。自身で入口を実行する場合はnullです。</returns>
    internal Task<ExecutionFrame>? ReadEnteredScope(CompiledWorkflow workflow, string nodeId, CancellationToken cancellationToken = default)
    {
        IterationRegion? region = workflow.Iterations?.Regions.FirstOrDefault(region => string.Equals(region.Start.Id, nodeId, StringComparison.Ordinal));
        if (region is null || downstream.Contains(nodeId) || !region.Members.Any(downstream.Contains))
        {
            return null;
        }
        return Read(nodeId, cancellationToken);
    }

    /// <summary>同じ合流の後続範囲を一度だけ構築し、到着ごとに大きな集合を複製しません。</summary>
    /// <returns>親から値を借用しない子実行の範囲です。</returns>
    internal static IReadOnlySet<string> Descendants(CompiledWorkflow workflow, string boundary)
    {
        HashSet<string> nodes = new(StringComparer.Ordinal) { boundary };
        foreach (NodeDefinition node in workflow.OrderedNodes)
        {
            if (workflow.Parents(node.Id).Any(nodes.Contains))
            {
                nodes.Add(node.Id);
            }
        }
        return nodes;
    }

    /// <summary>親の副作用を再実行せず、その結果を非起動の入力として借用します。</summary>
    /// <returns>同じ要求の確定を待つ値、または子実行自身が評価すべき場合はnullです。</returns>
    internal Task<ExecutionFrame>? Read(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (downstream.Contains(id))
        {
            return null;
        }
        Task<ExecutionFrame>? value = string.Equals(id, origin.NodeId, StringComparison.Ordinal) ? Task.FromResult(origin)
                                                                                                 : read(id);
        return value is null ? null
                             : BorrowAsync(value, cancellationToken);
    }

    /// <summary>確定した値と失敗は再実行せず借用し、未起動の値依存は通常の要求時評価に残します。</summary>
    /// <returns>値参照専用のフレームです。</returns>
    private static async Task<ExecutionFrame> BorrowAsync(Task<ExecutionFrame> value, CancellationToken cancellationToken = default)
    {
        // 親側の要求時評価もこの参照が所有するため、取消後も開始済みの評価を回収してから後続を止めます。
        ExecutionFrame frame = await value.ConfigureAwait(false);
        if (frame.Outcome == EOutcome.NotExecuted)
        {
            return frame;
        }
        return frame with { ConfigurationOnly = true, RetainedValue = true };
    }
}
