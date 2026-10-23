using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;

/// <summary>アーカイブを分割転送し、ファイル全体のサイズとSHA-256を検証します。</summary>
public static class ArchiveFileTransfer
{
    /// <summary>ファイルのサイズとSHA-256を登録し、受信済みの位置からアップロードします。</summary>
    /// <returns>アップロードしたファイルの転送情報。</returns>
    public static async Task<ArchiveTransfer> UploadAsync(Stream input, Guid operation,
        Func<string, object, CancellationToken, Task<JsonElement>> send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(send);
        if (!input.CanRead || !input.CanSeek || input.Length < 1 || ArchiveTransferLimits.MaximumBytes < input.Length)
        {
            throw new InvalidDataException("The archive must be a seekable file within the size limit.");
        }
        input.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        ArchiveTransfer file = new(operation, input.Length, hash);
        JsonElement accepted = await send("archive.upload.begin", file, cancellationToken).ConfigureAwait(false);
        long offset = accepted.GetInt64();
        if (offset < 0 || file.Length < offset)
        {
            throw new InvalidDataException("The receiver returned an invalid archive offset.");
        }
        input.Position = offset;
        byte[] buffer = new byte[ArchiveTransferLimits.ChunkBytes];
        while (offset < file.Length)
        {
            int count = (int)Math.Min(buffer.Length, file.Length - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            ArchiveChunk chunk = new(operation, offset, Convert.ToBase64String(buffer, 0, count));
            JsonElement reply = await send("archive.upload.chunk", chunk, cancellationToken).ConfigureAwait(false);
            if (reply.GetInt64() != offset + count)
            {
                throw new InvalidDataException("The archive upload acknowledgement does not match.");
            }
            offset += count;
        }
        return file;
    }

    /// <summary>操作IDと転送位置を確認しながらダウンロードし、最後に全体のSHA-256を照合します。</summary>
    /// <returns>ファイルのダウンロードと検証。</returns>
    public static async Task DownloadAsync(Stream output, ArchiveTransfer file,
        Func<string, object, CancellationToken, Task<JsonElement>> send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(send);
        if (!output.CanWrite || file.Length < 1 || ArchiveTransferLimits.MaximumBytes < file.Length)
        {
            throw new InvalidDataException("The archive transfer size is invalid.");
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < file.Length)
        {
            JsonElement response = await send("archive.read", new ArchiveReadRequest(file.OperationId, offset), cancellationToken).ConfigureAwait(false);
            ArchiveChunk chunk = response.Deserialize<ArchiveChunk>(ContractJson.Options) ?? throw new InvalidDataException("The archive chunk is missing.");
            if (chunk.OperationId != file.OperationId || chunk.Offset != offset || chunk.DataBase64 is null || ArchiveTransferLimits.ChunkBytes / 3 * 4 < chunk.DataBase64.Length)
            {
                throw new InvalidDataException("The archive chunk belongs to another operation or position.");
            }
            byte[] bytes = Convert.FromBase64String(chunk.DataBase64);
            if (bytes.Length == 0 || ArchiveTransferLimits.ChunkBytes < bytes.Length || file.Length - offset < bytes.Length)
            {
                throw new InvalidDataException("The archive chunk length is invalid.");
            }
            hash.AppendData(bytes);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded archive SHA-256 does not match.");
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
