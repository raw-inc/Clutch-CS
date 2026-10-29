using System.Globalization;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

/// <summary>ローカル保持した実行結果を、安定した続き位置とバイト上限で取得します。</summary>
public static class ExecutionHistory
{
    /// <summary>小さい出力は維持し、巨大な単一結果にも進むページを返します。</summary>
    /// <returns>有限の結果と次ページ位置です。</returns>
    public static ExecutionHistoryPage Page(IEnumerable<ExecutionResultView> reports, ExecutionHistoryQuery query) => Page(reports, query, false);

    /// <summary>活動表示用に、出力本文を含まない要約を完了順で取得します。</summary>
    /// <returns>長時間実行した結果も完了時刻に従って並ぶページです。</returns>
    public static ExecutionHistoryPage ActivityPage(IEnumerable<ExecutionResultView> reports, ExecutionHistoryQuery query) => Page(reports, query, true);

    /// <summary>用途ごとの並び順をカーソルと揃え、件数と転送量を制限します。</summary>
    /// <returns>取得した範囲と次の位置です。</returns>
    private static ExecutionHistoryPage Page(IEnumerable<ExecutionResultView> reports, ExecutionHistoryQuery query, bool activity)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 100 || query.MaximumBytes is < 16384 or > 1048576)
        {
            throw new ArgumentException("履歴の件数またはbyte上限が不正です。", nameof(query));
        }
        (long Time, Guid Id)? boundary = Decode(query.Cursor);
        ExecutionResultView[] candidates = reports.Where(report => boundary is null || Time(report, activity).UtcTicks < boundary.Value.Time
            || (Time(report, activity).UtcTicks == boundary.Value.Time && Identity(report).CompareTo(boundary.Value.Id) < 0))
            .OrderByDescending(report => Time(report, activity)).ThenByDescending(static report => Identity(report)).Take(query.Limit + 1).ToArray();
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        foreach (ExecutionResultView candidate in candidates.Take(query.Limit))
        {
            ExecutionResultView view = activity ? ActivitySummary(candidate)
                                                : ExecutionReportPresentation.Bound(candidate, Math.Min(131072, query.MaximumBytes / 2));
            entries.Add(view);
            string next = Encode(candidate, activity);
            if (query.MaximumBytes < JsonSerializer.SerializeToUtf8Bytes(new ExecutionHistoryPage(entries, next), ContractJson.Options).Length)
            {
                entries.RemoveAt(entries.Count - 1);
                break;
            }
            cursor = next;
        }
        if (entries.Count == candidates.Length)
        {
            cursor = null;
        }
        return new(entries, cursor);
    }

    /// <summary>用途ごとの時刻と実行IDで境界を作ります。</summary>
    /// <returns>次の参照へそのまま渡す位置です。</returns>
    private static string Encode(ExecutionResultView report, bool activity) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Time(report, activity).UtcTicks}:{Identity(report):N}")));

    /// <summary>完全結果と要約の同じ識別子を取得します。</summary>
    /// <returns>実行識別子です。</returns>
    private static Guid Identity(ExecutionResultView view) => view.Report?.Id ?? view.Summary!.Id;

    /// <summary>通常履歴は開始順、活動集計は完了順で取得します。</summary>
    /// <returns>指定した用途の境界時刻です。</returns>
    private static DateTimeOffset Time(ExecutionResultView view, bool activity)
    {
        if (activity)
        {
            return view.Report?.CompletedAt ?? view.Summary!.CompletedAt;
        }
        return view.Report?.StartedAt ?? view.Summary!.StartedAt;
    }

    /// <summary>活動量の計数に必要な識別子・時刻・成否だけを返します。</summary>
    /// <returns>ノードの出力・診断を含まない要約です。</returns>
    private static ExecutionResultView ActivitySummary(ExecutionResultView view)
    {
        ExecutionReportSummary summary;
        if (view.Report is { } report)
        {
            summary = new(report.Id, report.WorkflowId, report.Revision, report.Outcome, report.StartedAt, report.CompletedAt, [], report.Nodes.Count, 0);
        }
        else
        {
            summary = view.Summary! with { Targets = [] };
        }
        return new(null, summary);
    }

    /// <summary>不正な続き位置を明示拒否します。</summary>
    /// <returns>境界の時刻と実行IDです。</returns>
    private static (long Time, Guid Id)? Decode(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        if (128 < cursor.Length)
        {
            throw new ArgumentException("履歴cursorが不正です。", nameof(cursor));
        }
        string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
        string format = "N";
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time) || !Guid.TryParseExact(parts[1], format, out Guid id))
        {
            throw new ArgumentException("履歴cursorが不正です。", nameof(cursor));
        }
        return (time, id);
    }
}
