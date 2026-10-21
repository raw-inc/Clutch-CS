using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>完了した原実行IDを有限時間だけ保持し、Peerを跨ぐ二重計数を防ぎます。</summary>
internal sealed class OverviewActivityWindow
{
    private readonly Dictionary<Guid, (string Workflow, DateTimeOffset Time, EOutcome Outcome)> runs = [];
    private DateTimeOffset limitedUntil;

    /// <summary>取得上限による省略が生じたかです。</summary>
    internal bool CapacityLimited
    {
        get; private set;
    }

    /// <summary>新たに観測した完了結果を取り込みます。</summary>
    internal void Add(IReadOnlyList<ExecutionResultView> entries, DateTimeOffset now)
    {
        foreach (ExecutionResultView entry in entries)
        {
            Guid? id = entry.Report?.Id ?? entry.Summary?.Id;
            string? workflow = entry.Report?.WorkflowId ?? entry.Summary?.WorkflowId;
            DateTimeOffset? completed = entry.Report?.CompletedAt ?? entry.Summary?.CompletedAt;
            EOutcome? outcome = entry.Report?.Outcome ?? entry.Summary?.Outcome;
            if (id is not null && workflow is not null && completed is not null && outcome is not null && now.AddMinutes(-1) <= completed && completed <= now)
            {
                runs[id.Value] = (workflow, completed.Value, outcome.Value);
            }
        }
        foreach (Guid id in runs.Where(pair => pair.Value.Time < now.AddMinutes(-1)).Select(pair => pair.Key).ToArray())
        {
            runs.Remove(id);
        }
        if (2000 < runs.Count)
        {
            limitedUntil = now.AddMinutes(1);
        }
        CapacityLimited = now < limitedUntil;
        foreach (Guid id in runs.OrderByDescending(pair => pair.Value.Time).Skip(2000).Select(pair => pair.Key).ToArray())
        {
            runs.Remove(id);
        }
    }

    /// <summary>現在の60秒窓に存在する取得済み結果だけを集計します。</summary>
    /// <returns>ワークフロー別の完了頻度です。</returns>
    internal IReadOnlyList<OverviewActivity> Read(DateTimeOffset now) => runs.Values.Where(run => now.AddMinutes(-1) <= run.Time && run.Time <= now)
        .GroupBy(run => run.Workflow, StringComparer.Ordinal).Select(group => new OverviewActivity(group.Key, group.Count(), group.Count(run => run.Outcome != EOutcome.Succeeded)))
        .OrderByDescending(activity => activity.Completed).ThenBy(activity => activity.WorkflowId, StringComparer.Ordinal).ToArray();
}
