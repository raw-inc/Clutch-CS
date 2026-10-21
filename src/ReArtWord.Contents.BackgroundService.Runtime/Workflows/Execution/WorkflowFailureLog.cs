using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>トレース設定に依存せず、一件全体の非成功結果を容量制限付きで記録します。</summary>
internal static class WorkflowFailureLog
{
    /// <summary>ノード出力を複製せず、実行IDと失敗箇所を記録します。</summary>
    /// <returns>ログ障害が本来の結果へ伝播しない記録試行です。</returns>
    internal static ValueTask WriteAsync(IWorkflowLog? log, string peerId, ExecutionReport report, IReadOnlyList<WorkflowHookStep>? hooks, Exception? error = null,
        bool containsPersonalData = false, Guid? parentExecutionId = null, CancellationToken cancellationToken = default)
    {
        if (log is null || report.Outcome == EOutcome.Succeeded)
        {
            return ValueTask.CompletedTask;
        }
        string level = "ERROR";
        string message = "workflow.unsuccessful";
        if (report.Outcome == EOutcome.Cancelled)
        {
            level = "WARN";
            message = "workflow.cancelled";
        }
        NodeExecutionReport[] problems = report.Nodes.Where(node => node.Outcome is EOutcome.Failed or EOutcome.Unknown or EOutcome.Cancelled
            || node.Targets.Any(target => target.Result.Outcome == EOutcome.NotExecuted && !ExecutionResultReasons.IsExpectedSkip(target.Result.ReasonCode))).ToArray();
        NodeValue data = NodeValue.FromObject(new
        {
            executionId = report.Id,
            revision = Short(report.Revision),
            report.Outcome,
            report.StartedAt,
            report.CompletedAt,
            exceptionType = error?.GetType().Name,
            exception = error is null ? null
                                      : ExceptionDiagnostic.Capture(error, !containsPersonalData),
            parentExecutionId,
            unsuccessfulNodeCount = problems.Length,
            omittedNodeCount = Math.Max(0, problems.Length - 16),
            nodes = problems.OrderBy(node => node.Outcome == EOutcome.NotExecuted).Take(16).Select(node => new
                {
                    nodeId = Short(node.NodeId),
                    node.Outcome,
                    omittedTargetCount = Math.Max(0, node.Targets.Count(target => target.Result.Outcome != EOutcome.Succeeded) - 8),
                    targets = node.Targets.Where(target => target.Result.Outcome != EOutcome.Succeeded)
                        .OrderBy(target => target.Result.Outcome == EOutcome.NotExecuted && ExecutionResultReasons.IsExpectedSkip(target.Result.ReasonCode))
                        .ThenBy(target => target.Result.Outcome == EOutcome.NotExecuted).Take(8)
                        .Select(target => new
                        {
                            peerId = Short(target.PeerId),
                            target.Result.Outcome,
                            reasonCode = Short(target.Result.ReasonCode),
                            message = containsPersonalData ? null
                                                           : ExceptionDiagnostic.MessageWithinLimit(target.Result.Message, 512),
                        }),
                }),
        });
        return WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), report.CompletedAt, level, peerId, message,
            report.WorkflowId, CausationId: report.Id, Data: data, Hooks: hooks), cancellationToken);
    }

    /// <summary>自由な識別子や理由文字列でも終了ログの容量上限を維持します。</summary>
    /// <returns>長さを制限した値です。</returns>
    private static string? Short(string? value)
    {
        if (value is { Length: > 256 })
        {
            return value[..256] + "…";
        }
        return value;
    }
}
