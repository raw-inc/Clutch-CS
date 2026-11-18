using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;

/// <summary>実行元の経過時間を引き継ぎ、活動とノード診断の取得量を制限します。</summary>
internal static class DashboardExecutionReader
{
    /// <summary>新形式を確認済みのCoreでは、一時的な欠測を旧形式へ戻して日時で補いません。</summary>
    /// <returns>取得済みの結果と、未確認範囲を示すページです。</returns>
    internal static async Task<ExecutionHistoryPage?> ReadAsync(Func<string, ExecutionHistoryQuery, CancellationToken, Task<JsonElement?>> inspect,
        Guid instance, bool summariesOnly, bool observed, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        bool unconfirmed = false;
        do
        {
            long started = clock.GetTimestamp();
            ExecutionHistoryPage? page = await ReadPageAsync(inspect, "executions.observations", new(100, cursor, 65536, summariesOnly), timeout.Token).ConfigureAwait(false);
            if (page is null && !observed && cursor is null)
            {
                return await LegacyAsync(inspect, summariesOnly, clock.GetUtcNow(), timeout.Token).ConfigureAwait(false);
            }
            if (page?.ObservationInstanceId != instance || page.Entries.Any(view => view.CompletionAgeTicks is null or < 0 || view.ObservationSequence is null or < 1))
            {
                return new(entries, cursor, instance, true);
            }
            observed = true;
            unconfirmed |= page.AgeUnconfirmed;
            TimeSpan roundTrip = clock.GetElapsedTime(started);
            foreach (ExecutionResultView view in page.Entries)
            {
                TimeSpan age = TimeSpan.FromTicks(view.CompletionAgeTicks!.Value);
                TimeSpan initial = TimeSpan.MaxValue - age < roundTrip ? TimeSpan.MaxValue
                                                                       : age + roundTrip;
                entries.Add(view with { CompletionAge = new ObservationAge(clock, initial) });
            }
            if (page.Cursor is not null && string.Equals(cursor, page.Cursor, StringComparison.Ordinal))
            {
                return new(entries, cursor, instance, true);
            }
            cursor = page.Cursor;
        }
        while (summariesOnly && cursor is not null && entries.Count < 2000 && !timeout.IsCancellationRequested);
        return new(entries, cursor, instance, unconfirmed);
    }

    /// <summary>旧Coreとの接続を維持し、経過時間を取得できないことを集計の未確認範囲として伝えます。</summary>
    /// <returns>従来の日時による有限取得です。時計差を補正した結果ではありません。</returns>
    private static async Task<ExecutionHistoryPage?> LegacyAsync(Func<string, ExecutionHistoryQuery, CancellationToken, Task<JsonElement?>> inspect,
        bool summariesOnly, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        List<ExecutionResultView> entries = [];
        string? cursor = null;
        string operation = summariesOnly ? "executions.activity"
                                         : "executions.list";
        do
        {
            ExecutionHistoryPage? page = await ReadPageAsync(inspect, operation, new(100, cursor, 65536), cancellationToken).ConfigureAwait(false);
            if (page is null && cursor is null && summariesOnly)
            {
                page = await ReadPageAsync(inspect, "executions.list", new(100, MaximumBytes: 65536), cancellationToken).ConfigureAwait(false);
                return page is null ? null
                                    : page with { AgeUnconfirmed = true };
            }
            if (page is null)
            {
                return cursor is null ? null
                                      : new(entries, cursor, AgeUnconfirmed: true);
            }
            foreach (ExecutionResultView entry in page.Entries)
            {
                if (summariesOnly && (entry.Report?.CompletedAt ?? entry.Summary?.CompletedAt) is { } completed && completed < now.AddMinutes(-1))
                {
                    return new(entries, null, AgeUnconfirmed: true);
                }
                entries.Add(entry);
            }
            if (page.Cursor is not null && string.Equals(cursor, page.Cursor, StringComparison.Ordinal))
            {
                return new(entries, cursor, AgeUnconfirmed: true);
            }
            cursor = page.Cursor;
        }
        while (summariesOnly && cursor is not null && entries.Count < 2000 && !cancellationToken.IsCancellationRequested);
        return new(entries, cursor, AgeUnconfirmed: true);
    }

    /// <summary>一つの不正な応答で定期観測全体を終了させません。</summary>
    /// <returns>復元できたページだけです。</returns>
    private static async Task<ExecutionHistoryPage?> ReadPageAsync(Func<string, ExecutionHistoryQuery, CancellationToken, Task<JsonElement?>> inspect,
        string operation, ExecutionHistoryQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement? value = await inspect(operation, query, cancellationToken).ConfigureAwait(false);
            return value?.Deserialize<ExecutionHistoryPage>(ContractJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or OperationCanceledException)
        {
            return null;
        }
    }
}
