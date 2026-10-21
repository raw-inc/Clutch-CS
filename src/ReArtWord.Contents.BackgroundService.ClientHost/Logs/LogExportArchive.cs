using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>取得したページをテキストとJSONのペアにして、ZIPを保存せず逐次送信します。</summary>
internal static class LogExportArchive
{
    /// <summary>ZIPの索引も無制限に増えないよう、ファイルペア数に上限を設けます。</summary>
    private static int MaximumParts => 4096;

    /// <summary>同じ有限ページから両形式を書き、検索の二重実行や全件の一時保存を避けます。</summary>
    /// <returns>書き出した件数と取得範囲の完全性です。</returns>
    // 抑制理由: 具象のZIP・Streamを直後のConfigureAwait付きawait usingへ渡して所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "直後のawait usingが非同期解放を所有します。")]
    internal static async Task<(long Entries, bool Complete)> WriteAsync(Stream output, LogExportRequest request, IReadOnlyList<LogSearchSource> sources,
        Func<LogSearchSource, LogQuery, CancellationToken, Task<LogQueryResult?>> read,
        Func<LogSearchSource, LogNamesQuery, CancellationToken, Task<LogNames>> readNames, Action<long> progress, CancellationToken cancellationToken = default)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        LogSearchSession search = new(request.Query with { Limit = 1000, Cursor = null, MaximumBytes = 1048576 }, sources, read, 16777216, 1048576, 1048576);
        LogExportNames names = new(readNames);
        long count = 0, omitted = 0, fallback = 0;
        int part = 0;
        DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
        LogSearchPage page;
        ZipArchive zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (zip.ConfigureAwait(false))
        {
            // ヘッダーを先に送信し、低速な取得先を待つ間もHTTPの応答を開始します。
            await WriteJsonAsync(zip, "request.json", new { formatVersion = 1, exportedAt, request.TimeZoneId, request.Query, sources,
                maximumParts = MaximumParts, maximumEntriesPerPart = 1000, maximumLogBytesPerPart = 1048576 }, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            do
            {
                page = await search.NextAsync(cancellationToken).ConfigureAwait(false);
                List<NamedEntry> rows = new(page.Entries.Count);
                foreach (ScopedLogEntry[] group in page.Entries.Chunk(64))
                {
                    await names.PrepareAsync(group, cancellationToken).ConfigureAwait(false);
                    foreach (ScopedLogEntry row in group)
                    {
                        (string? workflow, string? node, string? revision, bool resolved) = names.Get(row);
                        rows.Add(new(row, workflow, node, revision, resolved));
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
                string stem = "logs-" + (++part).ToString("D5", CultureInfo.InvariantCulture);
                await WriteTextAsync(zip, stem + ".txt", rows, zone, cancellationToken).ConfigureAwait(false);
                Stream entry = await zip.CreateEntry(stem + ".json", CompressionLevel.Fastest).OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (entry.ConfigureAwait(false))
                {
                    using Utf8JsonWriter writer = new(entry, new JsonWriterOptions { Indented = true });
                    writer.WriteStartObject();
                    writer.WriteNumber("part", part);
                    writer.WriteStartArray("entries");
                    foreach (NamedEntry row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        JsonSerializer.Serialize(writer, new { clusterId = row.Row.Source.ClusterId, sourcePeerId = row.Row.Source.PeerId,
                            workflowName = row.Workflow, nodeName = row.Node, namesRevision = row.Revision, namesResolved = row.Resolved,
                            log = row.Row.Entry, omission = row.Row.Omission }, ContractJson.Options);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                count += rows.Count;
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                progress(count);
            }
            while (page.HasMore && part < MaximumParts);
            await WriteJsonAsync(zip, "summary.json", new { entries = count, parts = part, complete = page.Complete && omitted == 0 && !page.HasMore,
                unconfirmedSources = page.UnconfirmedSources, omittedEntries = omitted, nameFallbacks = fallback, partLimitReached = page.HasMore }, cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (count, page.Complete && omitted == 0 && !page.HasMore);
    }

    /// <summary>一件を一行に収め、指定タイムゾーンと論理名で読める形式へ整えます。</summary>
    /// <returns>一ページ分の本文の送信です。</returns>
    // 抑制理由: StreamとWriterをConfigureAwait付きの寿命へ直ちに引き渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ConfiguredAsyncDisposableが各資源の解放を所有します。")]
    private static async Task WriteTextAsync(ZipArchive zip, string name, IReadOnlyList<NamedEntry> rows, TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        Stream entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable entryLifetime = entry.ConfigureAwait(false);
        StreamWriter text = new(entry, new UTF8Encoding(false), 65536, leaveOpen: true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable textLifetime = text.ConfigureAwait(false);
        foreach (NamedEntry row in rows)
        {
            string date = TimeZoneInfo.ConvertTime(row.Row.Entry.Time, zone).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            await text.WriteLineAsync($"{date} [{SingleLine(row.Row.Entry.Level)}] {SingleLine(row.Workflow ?? "-")} {SingleLine(row.Node ?? "-")} {SingleLine(row.Row.Entry.Message)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await text.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>小さい検索条件・結果集計だけを独立したJSONへ記録します。</summary>
    /// <returns>メタデータの送信です。</returns>
    // 抑制理由: 取得したStreamをConfigureAwait付きの寿命へ直ちに引き渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ConfiguredAsyncDisposableがStreamの解放を所有します。")]
    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken cancellationToken = default)
    {
        Stream entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable entryLifetime = entry.ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(entry, value, ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一ログ一行を保ち、改行等の制御文字はJSON側に元のまま残します。</summary>
    /// <returns>ログ行へ埋め込める文字列です。</returns>
    internal static string SingleLine(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\0", "\\0", StringComparison.Ordinal);

    /// <summary>次の一ペアを書き終えるまでだけ、対応する名前と本文を参照します。</summary>
    private readonly record struct NamedEntry(ScopedLogEntry Row, string? Workflow, string? Node, string? Revision, bool Resolved);
}
