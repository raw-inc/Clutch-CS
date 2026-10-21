using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>ノードの詳細追跡と例外診断を共通ログの保護境界へ渡します。</summary>
public static class NodeExecutionLog
{
    /// <summary>ノード自身が結果へ変換する例外も、実行文脈を持ったまま記録します。</summary>
    /// <returns>本来処理へログ障害を伝えない記録試行です。</returns>
    public static ValueTask WriteExceptionAsync(NodeExecutionContext context, string reasonCode, Exception error, bool includeMessage = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        if (context.Log is null)
        {
            return ValueTask.CompletedTask;
        }
        return WriteExceptionAsync(context, reasonCode, ExceptionDiagnostic.Capture(error, includeMessage), cancellationToken);
    }

    /// <summary>子プロセスが返した診断にも通常ログの秘密値・個人情報保護を適用します。</summary>
    /// <returns>診断の受付です。</returns>
    public static ValueTask WriteExceptionAsync(NodeExecutionContext context, string reasonCode, ExceptionDiagnostic diagnostic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostic);
        return WorkflowLogBoundary.WriteAsync(context.Log, new(Guid.NewGuid(), context.TimeProvider.GetUtcNow(), "ERROR", context.PeerId, "node.exception",
            context.WorkflowId, context.NodeId, context.CausationId, NodeValue.FromObject(new
            {
                executionId = context.ExecutionId,
                revision = context.Revision,
                reasonCode,
                exception = diagnostic.Bound(),
            }), context.ContainsPersonalData, Hooks: context.Hooks), cancellationToken);
    }

    /// <summary>出力値を複製せず、ノード実行の成否・説明・例外を記録します。</summary>
    /// <returns>共通ログ境界に渡す診断値です。</returns>
    internal static NodeValue Result(Guid executionId, string revision, NodeResult result, Exception? error = null, string? targetPeerId = null)
        => NodeValue.FromObject(new
        {
            executionId,
            revision,
            targetPeerId,
            result.Outcome,
            result.ReasonCode,
            Message = ExceptionDiagnostic.MessageWithinLimit(result.Message),
            exception = error is null ? null
                                      : ExceptionDiagnostic.Capture(error),
        });

    /// <summary>明示した詳細追跡でのみ、開始前の入力を記録します。</summary>
    /// <returns>記録が不要なら割り当てを行わない受付です。</returns>
    internal static ValueTask WriteInputsAsync(IWorkflowLog? log, string peerId, TimeProvider clock, RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Trace || log is null)
        {
            return ValueTask.CompletedTask;
        }
        NodeValue? data = null;
        if (request.TraceSelection?.Inputs != false)
        {
            data = NodeValue.FromObject(request.Inputs);
        }
        return WriteAsync(log, peerId, clock, request, "DEBUG", "ノードの入力を取得しました。", data, cancellationToken);
    }

    /// <summary>入力・出力追跡の選択と、常時記録する失敗を同じ境界へ渡します。</summary>
    /// <returns>元の結果を変更しない記録試行です。</returns>
    internal static async ValueTask WriteResultAsync(IWorkflowLog? log, string peerId, TimeProvider clock, RemoteExecutionRequest request,
        NodeResult result, bool file, Exception? error = null, CancellationToken cancellationToken = default)
    {
        if (log is null)
        {
            return;
        }
        if (request.Trace)
        {
            NodeValue data = NodeValue.FromObject(new { result.Outcome, result.Route, result.ReasonCode });
            if (request.TraceSelection?.Outputs != false)
            {
                data = NodeValue.FromObject(result);
            }
            await WriteAsync(log, peerId, clock, request, "DEBUG", "ノードの結果を取得しました。", data, cancellationToken).ConfigureAwait(false);
        }
        if (result.Outcome is EOutcome.Failed or EOutcome.Unknown || error is not null || file && result.Outcome == EOutcome.NotExecuted)
        {
            string level = "ERROR";
            if (result.Outcome == EOutcome.NotExecuted && error is null)
            {
                level = "WARN";
            }
            await WriteAsync(log, peerId, clock, request, level, result.ReasonCode ?? "ノードの結果を確認してください。",
                Result(request.Id, request.Revision, result, error), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>元の要求の識別子と秘密値保護設定を一律適用します。</summary>
    /// <returns>通常ログへの受付です。</returns>
    private static ValueTask WriteAsync(IWorkflowLog? log, string peerId, TimeProvider clock, RemoteExecutionRequest request,
        string level, string message, NodeValue? data, CancellationToken cancellationToken = default)
        => WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), level, peerId, message, request.WorkflowId,
            request.Node.Id, request.CausationId, data, request.ContainsPersonalData, Hooks: request.Hooks), cancellationToken);
}
