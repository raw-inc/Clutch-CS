using System.Globalization;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

/// <summary>直近表示は完了を観測した順で辿り、履歴検索の日時と混同しません。</summary>
public static class ObservedExecutionHistory
{
    /// <summary>元の完了日時を保ったまま、実行元で測った経過時間を応答へ添えます。</summary>
    /// <returns>実際に経過時間を確認できた結果と、復元履歴による集計の未確認範囲です。</returns>
    public static ExecutionHistoryPage Page(IEnumerable<ExecutionResultView> reports, ExecutionHistoryQuery query, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(query);
        if (instanceId == Guid.Empty || query.Limit is < 1 or > 100 || query.MaximumBytes is < 16384 or > 1048576)
        {
            throw new ArgumentException("観測元・件数・応答容量が不正です。", nameof(query));
        }
        long? boundary = Decode(query.Cursor, instanceId);
        TimeSpan window = query.SummariesOnly ? TimeSpan.FromMinutes(1)
                                              : TimeSpan.FromMinutes(5);
        ExecutionResultView[] all = reports.ToArray();
        bool unconfirmed = all.Any(view => view.CompletionAge is null || view.CompletionAgeIsLowerBound && view.CompletionAge.Value.Elapsed <= window);
        ExecutionResultView[] candidates = all.Where(view => !view.CompletionAgeIsLowerBound && view.CompletionOrder is { } order
            && (boundary is null || order < boundary) && view.CompletionAge is { } age && age.Elapsed <= window)
            .OrderByDescending(view => view.CompletionOrder).Take(query.Limit + 1).ToArray();
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        foreach (ExecutionResultView candidate in candidates.Take(query.Limit))
        {
            ExecutionResultView value = query.SummariesOnly ? ExecutionHistory.ActivitySummary(candidate)
                                                            : ExecutionReportPresentation.Bound(candidate, Math.Min(131072, query.MaximumBytes / 2));
            value = value with { CompletionAgeTicks = candidate.CompletionAge!.Value.Elapsed.Ticks, ObservationSequence = candidate.CompletionOrder };
            entries.Add(value);
            string next = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"observed:{instanceId:N}:{candidate.CompletionOrder}")));
            if (query.MaximumBytes < JsonSize.Measure(new ExecutionHistoryPage(entries, next, instanceId, unconfirmed), ContractJson.Options))
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
        return new(entries, cursor, instanceId, unconfirmed);
    }

    /// <summary>別のCore実体や通常履歴の続き位置を、この観測へ流用しません。</summary>
    /// <returns>同じ実体で前のページまでに確認した完了順です。</returns>
    private static long? Decode(string? cursor, Guid instance)
    {
        if (cursor is null)
        {
            return null;
        }
        if (128 < cursor.Length)
        {
            throw new ArgumentException("観測の続き位置が不正です。", nameof(cursor));
        }
        string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
        string marker = "observed", format = "N";
        if (parts.Length != 3 || !string.Equals(parts[0], marker, StringComparison.Ordinal) || !Guid.TryParseExact(parts[1], format, out Guid owner)
            || owner != instance || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long order) || order < 1)
        {
            throw new ArgumentException("観測の続き位置または実体が不正です。", nameof(cursor));
        }
        return order;
    }
}
