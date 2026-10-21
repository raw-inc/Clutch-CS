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
    public static ExecutionHistoryPage Page(IEnumerable<ExecutionResultView> reports, ExecutionHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 100 || query.MaximumBytes is < 16384 or > 1048576)
        {
            throw new ArgumentException("履歴の件数またはbyte上限が不正です。", nameof(query));
        }
        (long Time, Guid Id)? boundary = Decode(query.Cursor);
        ExecutionResultView[] candidates = reports.Where(report => boundary is null || Started(report).UtcTicks < boundary.Value.Time
            || (Started(report).UtcTicks == boundary.Value.Time && Identity(report).CompareTo(boundary.Value.Id) < 0))
            .OrderByDescending(static report => Started(report)).ThenByDescending(static report => Identity(report)).Take(query.Limit + 1).ToArray();
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        foreach (ExecutionResultView candidate in candidates.Take(query.Limit))
        {
            ExecutionResultView view = ExecutionReportPresentation.Bound(candidate, Math.Min(131072, query.MaximumBytes / 2));
            entries.Add(view);
            string next = Encode(candidate);
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

    /// <summary>変更されない開始時刻と実行IDで境界を作ります。</summary>
    /// <returns>次の参照へそのまま渡す位置です。</returns>
    private static string Encode(ExecutionResultView report) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Started(report).UtcTicks}:{Identity(report):N}")));

    /// <summary>完全結果と要約の同じ識別子を取得します。</summary>
    /// <returns>実行識別子です。</returns>
    private static Guid Identity(ExecutionResultView view) => view.Report?.Id ?? view.Summary!.Id;

    /// <summary>完全結果と要約の同じ開始時刻を取得します。</summary>
    /// <returns>開始時刻です。</returns>
    private static DateTimeOffset Started(ExecutionResultView view) => view.Report?.StartedAt ?? view.Summary!.StartedAt;

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
