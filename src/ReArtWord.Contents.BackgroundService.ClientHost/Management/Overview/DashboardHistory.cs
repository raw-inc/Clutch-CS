using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;

/// <summary>ClientHostが保持する概要履歴の時間・件数制限です。</summary>
internal static class DashboardHistory
{
    /// <summary>同じノード・実行先の最新結果だけを、最大512件・5分間保持します。</summary>
    /// <returns>値や自由記述本文を含まない成否です。</returns>
    internal static IReadOnlyList<NodeExecutionObservation> NodeResults(IReadOnlyList<NodeExecutionObservation>? previous, ExecutionHistoryPage? current, string owner, DateTimeOffset now)
    {
        List<NodeExecutionObservation> values = [.. previous ?? []];
        foreach (ExecutionResultView entry in current?.Entries ?? [])
        {
            if (entry.Report is { } report)
            {
                foreach (NodeExecutionReport node in report.Nodes)
                {
                    if (node.Targets.Count == 0)
                    {
                        values.Add(new(report.WorkflowId, report.Revision, node.NodeId, owner, node.Outcome, null, report.CompletedAt));
                    }
                    foreach (TargetNodeResult target in node.Targets)
                    {
                        values.Add(new(report.WorkflowId, report.Revision, node.NodeId, target.PeerId, target.Result.Outcome, target.Result.ReasonCode, report.CompletedAt));
                    }
                }
            }
            else
            {
                if (entry.Summary is { } summary)
                {
                    values.AddRange(summary.Targets.Select(target => new NodeExecutionObservation(summary.WorkflowId, summary.Revision, target.NodeId, target.PeerId, target.Outcome, target.ReasonCode, summary.CompletedAt)));
                }
            }
        }
        return values.Where(value => now.AddMinutes(-5) <= value.CompletedAt && value.CompletedAt <= now)
            .OrderByDescending(value => value.CompletedAt).DistinctBy(value => (value.WorkflowId, value.NodeId, value.PeerId)).Take(512).ToArray();
    }

    /// <summary>活動集計に不要なノード出力を保持せず、60秒の確認済み完了要約を復元できます。</summary>
    /// <returns>最大2000件の原実行ID別の要約です。</returns>
    internal static ExecutionHistoryPage? Activity(ExecutionHistoryPage? previous, ExecutionHistoryPage? current, DateTimeOffset now)
    {
        if (current is null)
        {
            return null;
        }
        ExecutionReportSummary[] entries = current.Entries.Concat(previous?.Entries ?? []).Select(entry =>
        {
            if (entry.Report is { } report)
            {
                return new ExecutionReportSummary(report.Id, report.WorkflowId, report.Revision, report.Outcome, report.StartedAt, report.CompletedAt, [], report.Nodes.Count, 0);
            }
            return entry.Summary;
        }).OfType<ExecutionReportSummary>().Where(entry => now.AddMinutes(-1) <= entry.CompletedAt && entry.CompletedAt <= now)
            .DistinctBy(entry => entry.Id).OrderByDescending(entry => entry.CompletedAt).Take(2000).ToArray();
        return new(entries.Select(entry => new ExecutionResultView(null, entry with
        {
            Targets = []
        }, "Dashboard activity summary")).ToArray(), current.Cursor);
    }
}
