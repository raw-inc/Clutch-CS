using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

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
                        values.Add(new(report.WorkflowId, report.Revision, node.NodeId, owner, node.Outcome, null, report.CompletedAt)
                            { ExecutionId = report.Id, CompletionAge = entry.CompletionAge, SourceInstanceId = current!.ObservationInstanceId, CompletionSequence = entry.ObservationSequence });
                    }
                    foreach (TargetNodeResult target in node.Targets)
                    {
                        values.Add(new(report.WorkflowId, report.Revision, node.NodeId, target.PeerId, target.Result.Outcome, target.Result.ReasonCode, report.CompletedAt)
                            { ExecutionId = report.Id, CompletionAge = entry.CompletionAge, SourceInstanceId = current!.ObservationInstanceId, CompletionSequence = entry.ObservationSequence });
                    }
                }
            }
            else
            {
                if (entry.Summary is { } summary)
                {
                    values.AddRange(summary.Targets.Select(target => new NodeExecutionObservation(summary.WorkflowId, summary.Revision, target.NodeId, target.PeerId, target.Outcome, target.ReasonCode, summary.CompletedAt)
                        { ExecutionId = summary.Id, CompletionAge = entry.CompletionAge, SourceInstanceId = current!.ObservationInstanceId, CompletionSequence = entry.ObservationSequence }));
                }
            }
        }
        return values.DistinctBy(value => (value.WorkflowId, value.NodeId, value.PeerId, value.ExecutionId, value.CompletedAt))
            .Where(value => Recent(value.CompletionAge, value.CompletedAt, now, TimeSpan.FromMinutes(5)))
            .OrderByDescending(value => value.CompletionSequence)
            .ThenBy(value => value.CompletionAge?.Elapsed ?? now - value.CompletedAt)
            .DistinctBy(value => (value.WorkflowId, value.NodeId, value.PeerId)).Take(512).ToArray();
    }

    /// <summary>活動集計に不要なノード出力を保持せず、60秒の確認済み完了要約を復元できます。</summary>
    /// <returns>最大2000件の原実行ID別の要約です。</returns>
    internal static ExecutionHistoryPage? Activity(ExecutionHistoryPage? previous, ExecutionHistoryPage? current, DateTimeOffset now)
    {
        if (current is null)
        {
            return null;
        }
        ExecutionResultView[] entries = (previous?.Entries ?? []).Concat(current.Entries).Select(entry =>
        {
            if (entry.Report is { } report)
            {
                return entry with { Report = null, Summary = new(report.Id, report.WorkflowId, report.Revision, report.Outcome, report.StartedAt, report.CompletedAt, [], report.Nodes.Count, 0) };
            }
            return entry;
        }).Where(entry => entry.Summary is not null).DistinctBy(entry => entry.Summary!.Id)
            .Where(entry => Recent(entry.CompletionAge, entry.Summary!.CompletedAt, now, TimeSpan.FromMinutes(1)))
            .OrderBy(entry => entry.CompletionAge?.Elapsed ?? now - entry.Summary!.CompletedAt).Take(2000).ToArray();
        return current with { Entries = entries.Select(entry => entry with
        {
            Summary = entry.Summary! with { Targets = [] }, DetailReason = "Dashboard activity summary"
        }).ToArray() };
    }

    /// <summary>キャッシュの再読込みでも経過時間を進め、保持期限を延長しません。</summary>
    /// <returns>HTTP応答時点の経過時間を添えた活動要約です。</returns>
    internal static ExecutionHistoryPage? Present(ExecutionHistoryPage? page, DateTimeOffset now)
    {
        if (page is null)
        {
            return null;
        }
        return page with { Entries = page.Entries.Where(view => Recent(view.CompletionAge, view.Summary?.CompletedAt ?? view.Report!.CompletedAt, now, TimeSpan.FromMinutes(1)))
            .Select(view => view with { CompletionAgeTicks = view.CompletionAge?.Elapsed.Ticks }).ToArray() };
    }

    /// <summary>対応するCoreでは経過時間で判定し、旧形式だけ従来の日時比較を維持します。</summary>
    /// <returns>所定の表示期間に収まる結果かです。</returns>
    internal static bool Recent(ObservationAge? age, DateTimeOffset completed, DateTimeOffset now, TimeSpan window)
        => age is { } known ? known.Elapsed <= window
                            : now - window <= completed && completed <= now;
}
