using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

/// <summary>マニフェストで確定したバイト列を、同じバッファーとハッシュ状態を再利用して順次転送します。</summary>
public static class UpdateStreamPayload
{
    private static readonly ArrayPool<byte> buffers = ArrayPool<byte>.Create(1048576, 2);

    /// <summary>転送ごとの固定作業領域です。</summary>
    public static int BufferBytes => 1048576;

    /// <summary>制御要求を分割データごとに発行せず、宣言したファイルのバイト列だけを送ります。</summary>
    /// <returns>全ファイルの送出です。永続受付は受信側の結果で別途確認します。</returns>
    public static Task SendAsync(ReleaseManifest manifest, string root, Stream destination, CancellationToken cancellationToken = default)
        => SendAsync(manifest, root, destination, null, cancellationToken);

    /// <summary>バイト列を送出し、送信量を通知します。</summary>
    /// <returns>全ファイルの送出完了です。</returns>
    public static async Task SendAsync(ReleaseManifest manifest, string root, Stream destination, Action<long, long>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destination);
        ReleaseVerifier.Validate(manifest);
        FileAccessPolicy policy = new(new FileSystemOptions([root]));
        byte[] buffer = buffers.Rent(BufferBytes);
        long total = manifest.Files.Sum(file => file.Size);
        long transferred = 0;
        try
        {
            foreach (ReleaseFile file in manifest.Files)
            {
                string path = policy.ResolvePath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (source.Length != file.Size)
                {
                    throw new InvalidDataException("転送元の長さが変更されています。");
                }
                long remaining = file.Size;
                while (remaining != 0)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        throw new EndOfStreamException("転送元が途中で終了しました。");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    transferred += count;
                    progress?.Invoke(transferred, total);
                    remaining -= count;
                }
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffers.Return(buffer, clearArray: true);
        }
    }

    /// <summary>宣言した内容だけを保存し、ファイル単位のハッシュと永続flush後にペイロードへ公開します。</summary>
    /// <returns>全ファイルが検証・保存された総バイト数です。</returns>
    // 抑制理由: 成功応答より先にファイル内容を物理保存するため、FileStream.Flush(true)が必要です。
    [SuppressMessage("Performance", "CA1849", Justification = "耐久受付前の物理flushです。")]
    public static async Task<long> ReceiveAsync(LocalUpdateJob job, Guid token, Stream source, Action<int> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);
        ReleaseVerifier.Validate(job.Manifest);
        UpdateLayout layout = new(job.Options, job.OperationId);
        string temporary = Path.Combine(layout.OperationRoot, "stream-" + token.ToString("N"));
        layout.Validate(temporary);
        if (Directory.Exists(temporary))
        {
            throw new IOException("この転送の一時領域は既に存在します。");
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = buffers.Rent(BufferBytes);
        long total = 0;
        try
        {
            Directory.CreateDirectory(temporary);
            for (int index = 0; index < job.Manifest.Files.Count; index++)
            {
                ReleaseFile file = job.Manifest.Files[index];
                string pending = Path.Combine(temporary, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string destination = Path.Combine(layout.PayloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                layout.Validate(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                layout.Validate(destination);
                using (FileStream output = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long remaining = file.Size;
                    while (remaining != 0)
                    {
                        int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException("更新データが途中で終了しました。");
                        }
                        hash.AppendData(buffer.AsSpan(0, count));
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        remaining -= count;
                        total += count;
                        progress(count);
                    }
                    string actual = Convert.ToHexString(hash.GetHashAndReset());
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("更新データのchecksumが一致しません。");
                    }
                    output.Flush(flushToDisk: true);
                }
                layout.Validate(pending);
                layout.Validate(destination);
                File.Move(pending, destination, overwrite: false);
                if (file.Executable && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                {
                    File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("manifest外の余分なデータを受信しました。");
            }
            return total;
        }
        finally
        {
            buffers.Return(buffer, clearArray: true);
            layout.Validate(temporary);
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }
}
