using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>設定とログをZIP形式で読み書きします。ZIP内のファイルをディスクへ展開することはありません。</summary>
public static class ClusterArchiveFile
{
    /// <summary>ZIPファイルと展開後の内容に許容する最大バイト数。</summary>
    public static long MaximumBytes => ArchiveTransferLimits.MaximumBytes;
    private static int ConfigurationBytes => 16777216;
    private static int LogPartBytes => 1048576;
    private static int MaximumParts => 4096;
    private static string ConfigurationName => "configuration.json";

    /// <summary>設定と、取得時刻までのログをZIPへ書き出します。ログに欠落や省略があれば中止します。</summary>
    /// <returns>書き出したログ件数。</returns>
    // 抑制理由: ZIPと各entryはawait usingで所有し、呼出元のstreamだけを開いたまま返します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ZIPとentryをawait usingで非同期解放します。")]
    public static async Task<long> WriteAsync(Stream output, ClusterArchiveConfiguration configuration,
        Func<LogQuery, CancellationToken, Task<LogQueryResult>> readLogs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readLogs);
        ZipArchive zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = zip.ConfigureAwait(false);
        long total = await WriteEntryAsync(zip, ConfigurationName, configuration, ConfigurationBytes, cancellationToken).ConfigureAwait(false);
        long count = 0;
        if (!configuration.IncludesLogs)
        {
            return count;
        }
        string? cursor = null;
        int part = 0;
        do
        {
            LogQueryResult page = await readLogs(new(Until: configuration.CapturedAt, Limit: 1000, Cursor: cursor, MaximumBytes: LogPartBytes / 2), cancellationToken).ConfigureAwait(false);
            if (!page.Complete || 0 < page.Omissions?.Count || 0 < page.UnavailablePeers?.Count || MaximumParts <= part)
            {
                throw new InvalidDataException("Archive log export is incomplete.");
            }
            string name = "logs/" + (++part).ToString("D8", CultureInfo.InvariantCulture) + ".json";
            total += await WriteEntryAsync(zip, name, page.Entries, LogPartBytes, cancellationToken).ConfigureAwait(false);
            if (MaximumBytes < total)
            {
                throw new InvalidDataException("Archive contents exceed the size limit.");
            }
            count += page.Entries.Count;
            if (page.Cursor is not null && string.Equals(cursor, page.Cursor, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Archive log cursor did not advance.");
            }
            cursor = page.Cursor;
        }
        while (cursor is not null);
        return count;
    }

    /// <summary>ZIP内の全ファイルを読み取り、JSONの構文、サイズ、ログIDを検証します。</summary>
    /// <returns>復元対象の設定とログ件数。</returns>
    public static async Task<(ClusterArchiveConfiguration Configuration, long Logs)> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream file = File.OpenRead(path);
        using ZipArchive zip = new(file, ZipArchiveMode.Read);
        ValidateEntries(zip, file.Length);
        ClusterArchiveConfiguration configuration = await ReadEntryAsync<ClusterArchiveConfiguration>(zip.GetEntry(ConfigurationName)!, ConfigurationBytes, cancellationToken).ConfigureAwait(false);
        if (configuration.FormatVersion != 1 || string.IsNullOrWhiteSpace(configuration.ClusterId) || string.IsNullOrWhiteSpace(configuration.PeerId)
            || configuration.Operational is null || configuration.Workflows is null || configuration.Workflows.Any(workflow => workflow?.Saved?.Definition is null))
        {
            throw new InvalidDataException("Archive configuration is invalid or unsupported.");
        }
        HashSet<Guid> ids = [];
        foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => !string.Equals(entry.FullName, ConfigurationName, StringComparison.Ordinal)))
        {
            if (!configuration.IncludesLogs)
            {
                throw new InvalidDataException("Archive contains undeclared logs.");
            }
            WorkflowLogEntry[] logs = await ReadEntryAsync<WorkflowLogEntry[]>(entry, LogPartBytes, cancellationToken).ConfigureAwait(false);
            foreach (WorkflowLogEntry log in logs)
            {
                if (log is null || log.Id == Guid.Empty || !ids.Add(log.Id) || string.IsNullOrWhiteSpace(log.PeerId) || log.Message is null)
                {
                    throw new InvalidDataException("Archive log identity is invalid or duplicated.");
                }
                _ = Observability.Logging.LogPolicy.NormalizeLevel(log.Level);
            }
        }
        return (configuration, ids.Count);
    }

    /// <summary>メモリー使用量を抑えるため、ZIP内のログをファイル単位で読み取ります。</summary>
    /// <returns>ファイルごとのログ一覧。</returns>
    public static async IAsyncEnumerable<IReadOnlyList<WorkflowLogEntry>> ReadLogsAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileStream file = File.OpenRead(path);
        using ZipArchive zip = new(file, ZipArchiveMode.Read);
        ValidateEntries(zip, file.Length);
        foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => !string.Equals(entry.FullName, ConfigurationName, StringComparison.Ordinal)))
        {
            yield return await ReadEntryAsync<WorkflowLogEntry[]>(entry, LogPartBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>ZIP内のファイル名、重複、展開後のサイズを検査します。</summary>
    private static void ValidateEntries(ZipArchive zip, long bytes)
    {
        if (MaximumBytes < bytes || zip.Entries.Count is 0 || MaximumParts + 1 < zip.Entries.Count
            || zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count
            || zip.GetEntry(ConfigurationName) is null || MaximumBytes < zip.Entries.Sum(entry => entry.Length))
        {
            throw new InvalidDataException("Archive layout or size is invalid.");
        }
        string logPrefix = "logs/", suffix = ".json";
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, ConfigurationName, StringComparison.Ordinal))
            {
                if (ConfigurationBytes < entry.Length)
                {
                    throw new InvalidDataException("Archive configuration exceeds the size limit.");
                }
                continue;
            }
            string name = entry.FullName;
            if (LogPartBytes < entry.Length || name.Length != 18 || !name.StartsWith(logPrefix, StringComparison.Ordinal)
                || !name.EndsWith(suffix, StringComparison.Ordinal) || name.Skip(5).Take(8).Any(character => character < '0' || '9' < character))
            {
                throw new InvalidDataException("Archive contains an unsupported entry.");
            }
        }
    }

    /// <summary>実際の展開サイズが上限を超えていないことを確認し、JSONを読み取ります。</summary>
    /// <returns>JSONから読み取ったデータ。</returns>
    private static async Task<T> ReadEntryAsync<T>(ZipArchiveEntry entry, int maximum, CancellationToken cancellationToken = default)
    {
        if (entry.Length < 1 || maximum < entry.Length)
        {
            throw new InvalidDataException("Archive entry exceeds the size limit.");
        }
        using Stream input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = new byte[(int)entry.Length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("Archive entry length does not match.");
        }
        return JsonSerializer.Deserialize<T>(bytes, ContractJson.Options) ?? throw new InvalidDataException("Archive entry is empty.");
    }

    /// <summary>データのサイズを確認し、ZIP内のファイルに書き込みます。</summary>
    /// <returns>圧縮前のバイト数。</returns>
    // 抑制理由: 作成したentry streamは直後のawait usingが所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "entry streamはawait usingで非同期解放します。")]
    private static async Task<int> WriteEntryAsync<T>(ZipArchive zip, string name, T value, int maximum, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (maximum < bytes.Length)
        {
            throw new InvalidDataException("Archive entry exceeds the size limit.");
        }
        Stream output = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes.Length;
    }
}
