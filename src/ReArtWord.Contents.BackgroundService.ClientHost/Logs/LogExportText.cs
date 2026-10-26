using System.Globalization;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>検索結果を全件保持せず、詳細情報とともに一つのログファイルへ送信します。</summary>
internal static class LogExportText
{
    /// <summary>検索条件を固定して全ページを取得し、未取得範囲は末尾に記録します。</summary>
    /// <returns>出力済み件数と取得範囲の完全性です。</returns>
    // 抑制理由: Writerを直後のConfigureAwait付きawait usingへ渡して解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ConfiguredAsyncDisposableがWriterを解放し、呼出元のStreamは保持します。")]
    internal static async Task<(long Entries, bool Complete)> WriteAsync(Stream output, LogExportRequest request, IReadOnlyList<LogSearchSource> sources,
        Func<LogSearchSource, LogQuery, CancellationToken, Task<LogQueryResult?>> read,
        Func<LogSearchSource, LogNamesQuery, CancellationToken, Task<LogNames>> readNames, Action<long> progress, CancellationToken cancellationToken = default)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        LogSearchSession search = new(request.Query with { Limit = 1000, Cursor = null, MaximumBytes = 1048576 }, sources, read, 16777216, 1048576, 1048576);
        LogExportNames names = new(readNames);
        StreamWriter text = new(output, new UTF8Encoding(false), 65536, leaveOpen: true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable textLifetime = text.ConfigureAwait(false);
        await text.WriteLineAsync("# Clutch-CS log export".AsMemory(), cancellationToken).ConfigureAwait(false);
        string header = "# Request: " + JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.UtcNow, request.TimeZoneId, request.Query, sources }, ContractJson.Options);
        await text.WriteLineAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        await text.FlushAsync(cancellationToken).ConfigureAwait(false);
        long count = 0, omitted = 0, fallback = 0;
        LogSearchPage page;
        do
        {
            page = await search.NextAsync(cancellationToken).ConfigureAwait(false);
            foreach (ScopedLogEntry[] group in page.Entries.Chunk(64))
            {
                await names.PrepareAsync(group, cancellationToken).ConfigureAwait(false);
                foreach (ScopedLogEntry row in group)
                {
                    (string? workflow, string? node, string? revision, bool resolved) = names.Get(row);
                    string date = TimeZoneInfo.ConvertTime(row.Entry.Time, zone).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                    string line = $"{date} [{LogExportArchive.SingleLine(row.Entry.Level)}] [{LogExportArchive.SingleLine(row.Source.ClusterId)} / {LogExportArchive.SingleLine(row.Entry.PeerId)}] "
                        + $"{LogExportArchive.SingleLine(workflow ?? "-")} / {LogExportArchive.SingleLine(node ?? "-")} {LogExportArchive.SingleLine(row.Entry.Message)}";
                    await text.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    string record = "  record: " + JsonSerializer.Serialize(new { clusterId = row.Source.ClusterId, sourcePeerId = row.Source.PeerId,
                        workflowName = workflow, nodeName = node, namesRevision = revision, namesResolved = resolved, log = row.Entry, omission = row.Omission }, ContractJson.Options);
                    await text.WriteLineAsync(record.AsMemory(), cancellationToken).ConfigureAwait(false);
                    count++;
                    if (row.Omission is not null)
                    {
                        omitted++;
                    }
                    if (!resolved)
                    {
                        fallback++;
                    }
                }
            }
            await text.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress(count);
        }
        while (page.HasMore);
        bool complete = page.Complete && omitted == 0;
        string summary = "# Summary: " + JsonSerializer.Serialize(new { entries = count, complete, unconfirmedSources = page.UnconfirmedSources,
            omittedEntries = omitted, nameFallbacks = fallback }, ContractJson.Options);
        await text.WriteLineAsync(summary.AsMemory(), cancellationToken).ConfigureAwait(false);
        await text.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (count, complete);
    }
}
