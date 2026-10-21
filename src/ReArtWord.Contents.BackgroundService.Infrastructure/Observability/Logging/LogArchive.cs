using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ZstdSharp;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>レベル・日付別の不変ファイルとzstd移行を管理します。</summary>
internal sealed class LogArchive
{
    private readonly string directory;
    private readonly FileAccessPolicy access;

    /// <summary>専用ログ保存先を固定します。</summary>
    internal LogArchive(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        access = new(new FileSystemOptions([this.directory]));
    }

    /// <summary>既存ファイルを上書きせず一一括処理を原子的に公開します。</summary>
    /// <returns>ファイル保存完了です。</returns>
    internal async Task WriteAsync(LogSegment segment, IReadOnlyList<ReadOnlyMemory<byte>> entries, CancellationToken cancellationToken = default)
    {
        string path = PathFor(segment, false);
        if (File.Exists(path) || File.Exists(PathFor(segment, true)))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".next";
        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            foreach (ReadOnlyMemory<byte> entry in entries)
            {
                await output.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(new byte[] { 10 }, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        File.Move(temporary, path);
    }

    /// <summary>未加工のまたはzstdを一件ずつ読み込みます。</summary>
    /// <returns>保存済みログ列です。</returns>
    internal IAsyncEnumerable<WorkflowLogEntry> ReadAsync(LogSegment segment, CancellationToken cancellationToken = default)
        => ReadAsync(segment, false, cancellationToken);

    /// <summary>非圧縮限定では、検索中の圧縮完了を理由にzstdへ読み替えません。</summary>
    /// <returns>選択した形式の保存済みログ列です。</returns>
    internal async IAsyncEnumerable<WorkflowLogEntry> ReadAsync(LogSegment segment, bool uncompressedOnly, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileStream? input = Open(segment, uncompressedOnly, out bool compressed);
        if (input is null)
        {
            yield break;
        }
        if (compressed)
        {
            using DecompressionStream decompressed = new(input, leaveOpen: true);
            await foreach (WorkflowLogEntry entry in ReadLinesAsync(decompressed, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        else
        {
            await foreach (WorkflowLogEntry entry in ReadLinesAsync(input, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
    }

    /// <summary>読取り形式を確定し、圧縮完了と両形式の欠損を区別します。</summary>
    /// <returns>読取り可能な本文、または非圧縮検索の対象外ならnullです。</returns>
    private FileStream? Open(LogSegment segment, bool uncompressedOnly, out bool compressed)
    {
        compressed = segment.Compressed;
        if (uncompressedOnly && compressed)
        {
            return null;
        }
        string path = PathFor(segment, compressed);
        if (!uncompressedOnly && !File.Exists(path))
        {
            compressed = !compressed;
            path = PathFor(segment, compressed);
        }
        try
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) when (uncompressedOnly && File.Exists(PathFor(segment, true)))
        {
            return null;
        }
    }

    /// <summary>元未加工のを維持したまま圧縮ファイルを完成させます。</summary>
    /// <returns>完成した圧縮状態です。</returns>
    internal async Task<LogSegment> CompressAsync(LogSegment segment, CancellationToken cancellationToken = default)
    {
        string raw = PathFor(segment, false);
        string compressed = PathFor(segment, true);
        if (!File.Exists(compressed))
        {
            string temporary = compressed + ".next";
            await ConvertAsync(raw, temporary, true, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, compressed);
        }
        return segment with { Compressed = true };
    }

    /// <summary>圧縮ファイルを残して通常ファイルを復元します。</summary>
    /// <returns>再展開した状態です。</returns>
    internal async Task<LogSegment> ExpandAsync(LogSegment segment, CancellationToken cancellationToken = default)
    {
        string raw = PathFor(segment, false);
        if (!File.Exists(raw))
        {
            string temporary = raw + ".next";
            await ConvertAsync(PathFor(segment, true), temporary, false, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, raw);
        }
        return segment with { Compressed = false };
    }

    /// <summary>一つの形式変換で入出力ストリームを確実に閉じます。</summary>
    /// <returns>移動可能な完成ファイルです。</returns>
    private static async Task ConvertAsync(string source, string destination, bool compress, CancellationToken cancellationToken = default)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (compress)
        {
            CompressionStream compressed = new(output, leaveOpen: true);
            await using (compressed.ConfigureAwait(false))
            {
                await input.CopyToAsync(compressed, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            using DecompressionStream expanded = new(input, leaveOpen: true);
            await expanded.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>索引確定後に旧形式だけを削除します。</summary>
    internal void DeleteOldForm(LogSegment segment)
    {
        File.Delete(PathFor(segment, !segment.Compressed));
    }

    /// <summary>期限が到来したファイルの両形式を削除します。</summary>
    internal void Delete(LogSegment segment)
    {
        File.Delete(PathFor(segment, false));
        File.Delete(PathFor(segment, true));
    }

    /// <summary>残存ファイルから索引再構成候補を見つけます。</summary>
    /// <returns>レベル・日付・識別子を持つ候補です。</returns>
    internal IReadOnlyList<LogSegment> Discover(bool uncompressedOnly = false)
    {
        access.ValidatePath(directory, true);
        Directory.CreateDirectory(directory);
        List<LogSegment> candidates = [];
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        foreach (string path in Directory.EnumerateFiles(directory, "*", options))
        {
            string[] parts = Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar);
            if (parts.Length != 3)
            {
                continue;
            }
            string rawSuffix = ".jsonl";
            string compressedSuffix = ".jsonl.zst";
            bool compressed = parts[2].EndsWith(compressedSuffix, StringComparison.Ordinal);
            if (uncompressedOnly && compressed || !compressed && !parts[2].EndsWith(rawSuffix, StringComparison.Ordinal))
            {
                continue;
            }
            string stem = parts[2].Split('.')[0];
            string[] identity = stem.Split('-');
            string format = "yyyy-MM-dd";
            string identifierFormat = "N";
            if (identity.Length != 2 || !Guid.TryParseExact(identity[1], identifierFormat, out Guid id) || !DateOnly.TryParseExact(parts[1], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                throw new IOException("ログfileの識別情報が不正です。");
            }
            string local = "local";
            candidates.Add(new(id, LogPolicy.NormalizeLevel(parts[0]), date, [], 0, compressed, string.Equals(identity[0], local, StringComparison.Ordinal), []));
        }
        return candidates;
    }

    /// <summary>設定の保存先外へファイル名を解決しません。</summary>
    /// <returns>このsegmentの実パスです。</returns>
    private string PathFor(LogSegment segment, bool compressed)
    {
        string origin = "received";
        if (segment.LocalOrigin)
        {
            origin = "local";
        }
        string suffix = ".jsonl";
        if (compressed)
        {
            suffix += ".zst";
        }
        return access.ResolvePath(Path.Combine(directory, LogPolicy.NormalizeLevel(segment.Level), segment.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), origin + "-" + segment.Id.ToString("N") + suffix));
    }

    /// <summary>一行の上限を持つストリーム読取りで巨大ファイル全体をメモリへ載せません。</summary>
    /// <returns>JSON lineごとのログです。</returns>
    private static async IAsyncEnumerable<WorkflowLogEntry> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        using MemoryStream line = new();
        try
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            while (read != 0)
            {
                int offset = 0;
                while (offset < read)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int separator = buffer.AsSpan(offset, read - offset).IndexOf((byte)10);
                    int count = read - offset;
                    if (0 <= separator)
                    {
                        count = separator;
                    }
                    if (268435456 < line.Length + count)
                    {
                        throw new IOException("ログ一件の読取り上限を超えています。");
                    }
                    if (0 <= separator && line.Length == 0)
                    {
                        WorkflowLogEntry entry = JsonSerializer.Deserialize<WorkflowLogEntry>(buffer.AsSpan(offset, count)) ?? throw new IOException("ログJSONが不正です。");
                        offset += count + 1;
                        yield return entry;
                        continue;
                    }
                    await line.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                    offset += count;
                    if (0 <= separator)
                    {
                        WorkflowLogEntry entry = JsonSerializer.Deserialize<WorkflowLogEntry>(line.GetBuffer().AsSpan(0, checked((int)line.Length))) ?? throw new IOException("ログJSONが不正です。");
                        line.SetLength(0);
                        offset++;
                        yield return entry;
                    }
                }
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            if (line.Length != 0)
            {
                throw new IOException("ログfileが一件の途中で切れています。");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
