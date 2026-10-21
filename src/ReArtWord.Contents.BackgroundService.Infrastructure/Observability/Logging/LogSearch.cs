using System.Globalization;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ZLinq;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>保持中のraw/zstdを有限件数の結果へ絞り込みます。</summary>
internal static class LogSearch
{
    /// <summary>全ファイルをメモリへ展開せず最新の一ページを検索します。</summary>
    /// <returns>検索結果と続き位置、完全性です。</returns>
    internal static async Task<LogQueryResult> QueryAsync(LogStore store, LogQuery query, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 1000 || query.MaximumBytes is < 16384 or > 1048576 || (query.From is not null && query.Until is not null && query.Until < query.From))
        {
            throw new ArgumentException("ログ検索範囲または件数が不正です。", nameof(query));
        }
        (long Time, Guid Id)? cursor = DecodeCursor(query.Cursor);
        Comparer<WorkflowLogEntry> comparer = Comparer<WorkflowLogEntry>.Create(Compare);
        PriorityQueue<LogPageEntry, WorkflowLogEntry> result = new(comparer);
        bool complete = true;
        foreach (LogSegment segment in store.Segments.AsValueEnumerable().Where(segment => !query.UncompressedOnly || !segment.Compressed)
            .OrderByDescending(segment => segment.LastTime ?? DateTimeOffset.MaxValue).ToArray())
        {
            if (query.Levels is { Count: > 0 } && !query.Levels.Contains(segment.Level, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            DateTimeOffset? oldest = null;
            if (result.Count == query.Limit + 1)
            {
                oldest = result.Peek().Entry.Time;
            }
            if (Outside(segment, query, cursor?.Time, oldest))
            {
                continue;
            }
            try
            {
                await foreach (WorkflowLogEntry stored in store.ReadAsync(segment, query.UncompressedOnly, cancellationToken).ConfigureAwait(false))
                {
                    if (!AfterCursor(stored, cursor) || result.Count == query.Limit + 1 && Compare(stored, result.Peek().Entry) < 0)
                    {
                        continue;
                    }
                    WorkflowLogEntry entry = LogRedactor.Redact(stored, patterns);
                    if (!Matches(entry, query) || !AfterCursor(entry, cursor))
                    {
                        continue;
                    }
                    LogPageEntry bounded = LogPageEntry.Create(entry, Math.Min(131072, query.MaximumBytes / 2));
                    result.Enqueue(bounded, bounded.Entry);
                    if (query.Limit + 1 < result.Count)
                    {
                        result.Dequeue();
                    }
                }
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                complete = false;
            }
        }
        LogPageEntry[] ordered = result.UnorderedItems.Select(item => item.Element).OrderByDescending(static item => item.Entry, comparer).ToArray();
        List<WorkflowLogEntry> entries = [];
        List<LogEntryOmission> omissions = [];
        string? next = null;
        foreach (LogPageEntry item in ordered.Take(query.Limit))
        {
            entries.Add(item.Entry);
            if (item.Omission is not null)
            {
                omissions.Add(item.Omission);
            }
            string candidate = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{item.Entry.Time.UtcTicks}:{item.Entry.Id:N}")));
            if (query.MaximumBytes < JsonSerializer.SerializeToUtf8Bytes(new LogQueryResult(entries, candidate, complete, Omissions: omissions), ContractJson.Options).Length)
            {
                entries.RemoveAt(entries.Count - 1);
                if (item.Omission is not null)
                {
                    omissions.RemoveAt(omissions.Count - 1);
                }
                break;
            }
            next = candidate;
        }
        if (entries.Count == ordered.Length)
        {
            next = null;
        }
        return new(entries, next, complete, Omissions: omissions);
    }

    /// <summary>確認済みの最小・最大時刻だけで、検索に入らない不変ファイルを除きます。</summary>
    /// <returns>同時刻のID比較や旧索引を省略しない、安全な範囲外判定です。</returns>
    private static bool Outside(LogSegment segment, LogQuery query, long? cursor, DateTimeOffset? oldest)
        => segment.FirstTime is { } first && segment.LastTime is { } last && first <= last
            && (query.From is { } from && last < from || query.Until is { } until && until < first || cursor is { } time && time < first.UtcTicks || oldest is { } minimum && last < minimum);

    /// <summary>同時刻も識別子で安定した順序にします。</summary>
    /// <returns>古い順の比較結果です。</returns>
    private static int Compare(WorkflowLogEntry first, WorkflowLogEntry second)
    {
        int time = first.Time.CompareTo(second.Time);
        if (time != 0)
        {
            return time;
        }
        return first.Id.CompareTo(second.Id);
    }

    /// <summary>表示前に保護した値で検索条件を評価します。</summary>
    /// <returns>条件が一致するかです。</returns>
    private static bool Matches(WorkflowLogEntry entry, LogQuery query)
    {
        if ((query.From is DateTimeOffset from && entry.Time < from) || (query.Until is DateTimeOffset until && until < entry.Time))
        {
            return false;
        }
        if (query.CausationId is Guid causation && entry.CausationId != causation)
        {
            return false;
        }
        if (query.Peers is { Count: > 0 } && !query.Peers.Contains(entry.PeerId, StringComparer.Ordinal))
        {
            return false;
        }
        if ((query.WorkflowId is not null && !string.Equals(entry.WorkflowId, query.WorkflowId, StringComparison.Ordinal))
            || (query.NodeId is not null && !string.Equals(entry.NodeId, query.NodeId, StringComparison.Ordinal)))
        {
            return false;
        }
        if (query.Text is not null && !entry.Message.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
            && !JsonSerializer.Serialize(entry.Data).Contains(query.Text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    /// <summary>ページ境界以降の古いログだけを選択します。</summary>
    /// <returns>次ページへ含めるかです。</returns>
    private static bool AfterCursor(WorkflowLogEntry entry, (long Time, Guid Id)? cursor)
    {
        if (cursor is null)
        {
            return true;
        }
        return entry.Time.UtcTicks < cursor.Value.Time || (entry.Time.UtcTicks == cursor.Value.Time && entry.Id.CompareTo(cursor.Value.Id) < 0);
    }

    /// <summary>不正または巨大な続き位置を拒否します。</summary>
    /// <returns>安定した時刻と識別子です。</returns>
    private static (long Time, Guid Id)? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        if (128 < cursor.Length)
        {
            throw new ArgumentException("ログcursorが不正です。", nameof(cursor));
        }
        string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
        string format = "N";
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time) || !Guid.TryParseExact(parts[1], format, out Guid id))
        {
            throw new ArgumentException("ログcursorが不正です。", nameof(cursor));
        }
        return (time, id);
    }
}
