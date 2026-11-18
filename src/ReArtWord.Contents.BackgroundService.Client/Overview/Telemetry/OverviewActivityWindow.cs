using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>完了した原実行IDを有限時間だけ保持し、Peerを跨ぐ二重計数を防ぎます。</summary>
internal sealed class OverviewActivityWindow(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<Guid, (string Workflow, DateTimeOffset Time, EOutcome Outcome, ObservationAge? Age)> runs = [];
    private ObservationAge? limitedAge;

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
            if (id is not null && workflow is not null && completed is not null && outcome is not null && Recent(entry.CompletionAge, completed.Value, now))
            {
                runs.TryAdd(id.Value, (workflow, completed.Value, outcome.Value, entry.CompletionAge));
            }
        }
        foreach (Guid id in runs.Where(pair => !Recent(pair.Value.Age, pair.Value.Time, now)).Select(pair => pair.Key).ToArray())
        {
            runs.Remove(id);
        }
        if (2000 <= runs.Count)
        {
            limitedAge = new(clock);
        }
        CapacityLimited = limitedAge is { } age && age.Elapsed < TimeSpan.FromMinutes(1);
        foreach (Guid id in runs.OrderBy(pair => pair.Value.Age?.Elapsed ?? now - pair.Value.Time).Skip(2000).Select(pair => pair.Key).ToArray())
        {
            runs.Remove(id);
        }
    }

    /// <summary>現在の60秒窓に存在する取得済み結果だけを集計します。</summary>
    /// <returns>ワークフロー別の完了頻度です。</returns>
    internal IReadOnlyList<OverviewActivity> Read(DateTimeOffset now) => runs.Values.Where(run => Recent(run.Age, run.Time, now))
        .GroupBy(run => run.Workflow, StringComparer.Ordinal).Select(group => new OverviewActivity(group.Key, group.Count(), group.Count(run => run.Outcome != EOutcome.Succeeded)))
        .OrderByDescending(activity => activity.Completed).ThenBy(activity => activity.WorkflowId, StringComparer.Ordinal).ToArray();

    /// <summary>対応する観測は経過時間だけで集計し、旧応答のみ従来の日時比較を使います。</summary>
    /// <returns>直近60秒に含まれるかです。</returns>
    private static bool Recent(ObservationAge? age, DateTimeOffset completed, DateTimeOffset now)
        => age is { } known ? known.Elapsed <= TimeSpan.FromMinutes(1)
                            : now.AddMinutes(-1) <= completed && completed <= now;
}
