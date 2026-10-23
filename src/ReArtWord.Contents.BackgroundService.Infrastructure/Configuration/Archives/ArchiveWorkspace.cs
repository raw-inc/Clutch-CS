using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>操作IDごとの転送資材と結果を保存し、利用者からファイルパスを受け取りません。</summary>
internal sealed class ArchiveWorkspace(string root)
{
    internal static int ChunkBytes => ArchiveTransferLimits.ChunkBytes;

    /// <summary>入力IDから一つの操作フォルダーだけを解決します。</summary>
    /// <returns>この操作が所有する保存先です。</returns>
    internal string FileFor(Guid id, string name)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        string directory = Path.Combine(root, id.ToString("N"));
        return Path.Combine(directory, name);
    }

    /// <summary>完了・準備・固定入力を置換保存します。</summary>
    /// <returns>保存の確認です。</returns>
    internal Task WriteAsync<T>(Guid id, string name, T value, CancellationToken cancellationToken = default)
    {
        string path = FileFor(id, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return UpdateProgressFile.WriteAsync(path, value, cancellationToken);
    }

    /// <summary>保存済みの元情報を読みます。</summary>
    /// <returns>未作成ならnullです。</returns>
    internal async Task<T?> ReadAsync<T>(Guid id, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            return await UpdateProgressFile.ReadAsync<T>(FileFor(id, name), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
    }

    /// <summary>同じIDの処理を一度だけ開始し、途中終了後に再実行しません。</summary>
    internal void Claim(Guid id)
    {
        using FileStream marker = new(FileFor(id, "started"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
        marker.WriteByte(1);
        marker.Flush(true);
    }

    /// <summary>開始済みの入力を後から変更させません。</summary>
    internal void RequireNotStarted(Guid id)
    {
        string marker = FileFor(id, "started");
        if (File.Exists(marker))
        {
            throw new InvalidOperationException("This archive operation has already started. Query its original result.");
        }
    }

    /// <summary>全体長とハッシュを固定し、再開時も別内容へ変更させません。</summary>
    /// <returns>既に受信したバイト数です。</returns>
    internal async Task<long> BeginAsync(ArchiveTransfer request, CancellationToken cancellationToken = default)
    {
        RequireNotStarted(request.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sha256);
        if (request.Length < 1 || ClusterArchiveFile.MaximumBytes < request.Length || request.Sha256.Length != 64 || Convert.FromHexString(request.Sha256).Length != 32)
        {
            throw new ArgumentException("Invalid archive length or SHA-256.", nameof(request));
        }
        ArchiveTransfer? existing = await ReadAsync<ArchiveTransfer>(request.OperationId, "upload.json", cancellationToken).ConfigureAwait(false);
        if (existing is not null && !string.Equals(ContractJson.Hash(existing), ContractJson.Hash(request), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The archive operation already belongs to different contents.");
        }
        await WriteAsync(request.OperationId, "upload.json", request, cancellationToken).ConfigureAwait(false);
        FileInfo file = new(FileFor(request.OperationId, "archive.zip"));
        return file.Exists ? file.Length
                           : 0;
    }

    /// <summary>同じ位置の再送を照合し、追記済み内容を上書きしません。</summary>
    /// <returns>受信済みの末尾位置です。</returns>
    internal async Task<long> AppendAsync(ArchiveChunk chunk, CancellationToken cancellationToken = default)
    {
        RequireNotStarted(chunk.OperationId);
        string preview = FileFor(chunk.OperationId, "plan.json");
        if (File.Exists(preview))
        {
            throw new InvalidOperationException("The archive was already previewed.");
        }
        ArchiveTransfer expected = await ReadAsync<ArchiveTransfer>(chunk.OperationId, "upload.json", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Begin the archive upload first.");
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.DataBase64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.DataBase64.Length, ChunkBytes / 3 * 4);
        byte[] bytes = Convert.FromBase64String(chunk.DataBase64);
        ArgumentOutOfRangeException.ThrowIfEqual(bytes.Length, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, ChunkBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(chunk.Offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.Offset, expected.Length - bytes.Length);
        using FileStream file = new(FileFor(chunk.OperationId, "archive.zip"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (file.Length < chunk.Offset)
        {
            throw new InvalidDataException("Archive chunks must be contiguous.");
        }
        file.Position = chunk.Offset;
        if (chunk.Offset < file.Length)
        {
            if (file.Length - chunk.Offset < bytes.Length)
            {
                throw new InvalidDataException("Archive chunk overlaps the received boundary.");
            }
            byte[] received = new byte[bytes.Length];
            await file.ReadExactlyAsync(received, cancellationToken).ConfigureAwait(false);
            if (!received.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("Archive retransmission changed its contents.");
            }
            return file.Length;
        }
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        return file.Length;
    }

    /// <summary>全内容を検証してから、プレビューや復元に渡します。</summary>
    /// <returns>確認した転送条件です。</returns>
    internal async Task<ArchiveTransfer> VerifyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArchiveTransfer expected = await ReadAsync<ArchiveTransfer>(id, "upload.json", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Archive upload metadata is missing.");
        ArchiveTransfer actual = await DescribeAsync(id, cancellationToken).ConfigureAwait(false);
        if (expected.Length != actual.Length || !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The received archive does not match its length or SHA-256.");
        }
        return actual;
    }

    /// <summary>完成したZIPの転送情報を計算します。</summary>
    /// <returns>全体長と検査値です。</returns>
    internal async Task<ArchiveTransfer> DescribeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using FileStream file = File.OpenRead(FileFor(id, "archive.zip"));
        if (ClusterArchiveFile.MaximumBytes < file.Length)
        {
            throw new InvalidDataException("Archive exceeds the size limit.");
        }
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        return new(id, file.Length, hash);
    }

    /// <summary>完成済み書出しの一断片を読みます。</summary>
    /// <returns>要求した位置のバイト列です。</returns>
    internal async Task<ArchiveChunk> ReadChunkAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        ArchiveOperationResult? result = await ReadAsync<ArchiveOperationResult>(request.OperationId, "result.json", cancellationToken).ConfigureAwait(false);
        if (result?.File is null || !result.Completed || result.Outcome != Contracts.Execution.EOutcome.Succeeded || request.Offset < 0 || result.File.Length < request.Offset)
        {
            throw new InvalidOperationException("The archive export is not available at this offset.");
        }
        using FileStream file = File.OpenRead(FileFor(request.OperationId, "archive.zip"));
        if (file.Length != result.File.Length)
        {
            throw new InvalidDataException("The exported archive length changed.");
        }
        if (request.Offset == 0)
        {
            await WriteAsync(request.OperationId, "result.json", result, cancellationToken).ConfigureAwait(false);
        }
        file.Position = request.Offset;
        byte[] bytes = new byte[(int)Math.Min(ChunkBytes, file.Length - request.Offset)];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new(request.OperationId, request.Offset, Convert.ToBase64String(bytes));
    }

    /// <summary>完了済み資材と未開始の古い転送だけを整理し、操作記録と未確認の入力を保持します。</summary>
    /// <returns>24時間を過ぎた大きな資材の回収です。</returns>
    internal async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string format = "N";
            if (!Guid.TryParseExact(Path.GetFileName(directory), format, out Guid id))
            {
                continue;
            }
            ArchiveOperationResult? result = await ReadAsync<ArchiveOperationResult>(id, "result.json", cancellationToken).ConfigureAwait(false);
            string completed = FileFor(id, "result.json"), upload = FileFor(id, "upload.json"), marker = FileFor(id, "started");
            bool finished = result is { Completed: true, Outcome: Contracts.Execution.EOutcome.Succeeded or Contracts.Execution.EOutcome.NotExecuted } && File.GetLastWriteTimeUtc(completed) < cutoff;
            string plan = FileFor(id, "plan.json"), payload = FileFor(id, "archive.zip");
            bool abandoned = result is null && !File.Exists(marker) && File.Exists(upload) && File.GetLastWriteTimeUtc(upload) < cutoff
                && (!File.Exists(plan) || File.GetLastWriteTimeUtc(plan) < cutoff)
                && (!File.Exists(payload) || File.GetLastWriteTimeUtc(payload) < cutoff);
            if (!finished && !abandoned)
            {
                continue;
            }
            foreach (string name in new[] { "archive.zip", "archive.tmp", "plan.json" })
            {
                File.Delete(FileFor(id, name));
            }
        }
    }
}
