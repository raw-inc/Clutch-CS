using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Files;

/// <summary>ブラウザーからの2MiB転送片を、相手が対応する有限の転送単位で中継します。</summary>
internal static class BrowserFileUploads
{
    private static readonly SemaphoreSlim slots = new(4);

    /// <summary>分割送信の要求を受け付けます。ファイル確定は別の明示要求です。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapPost("/client/browser-files/write", WriteAsync);
        app.MapPost("/client/browser-files/manifest", (FileManifestEntry[] entries) =>
        {
            if (entries.Length is < 1 or > 4096 || entries.Any(entry => entry is null || entry.RelativePath is null || 1024 < entry.RelativePath.Length))
            {
                throw new ArgumentException("The browser transfer manifest is too large.", nameof(entries));
            }
            return Results.Json(new { sha256 = FileTransferManifest.ComputeSha256(entries) });
        });
    }

    /// <summary>サイズと内容を検証してから、固定Peerの段階へ順に中継します。</summary>
    /// <returns>全内部転送片が確認済みになった応答です。</returns>
    private static async Task<IResult> WriteAsync(HttpContext context, PeerManagementClient remote, CancellationToken cancellationToken = default)
    {
        FileTransferPeer target = new(context.Request.Headers["X-Contents-Cluster"].ToString(), context.Request.Headers["X-Contents-Peer"].ToString(), Guid.Parse(context.Request.Headers["X-Contents-Instance"].ToString()));
        IQueryCollection query = context.Request.Query;
        FileStageToken stage = new(Guid.Parse(query["stage"].ToString()));
        int count = int.Parse(query["count"].ToString(), CultureInfo.InvariantCulture);
        long offset = long.Parse(query["offset"].ToString(), CultureInfo.InvariantCulture), length = long.Parse(query["length"].ToString(), CultureInfo.InvariantCulture);
        string hash = query["sha256"].ToString(), partHash = query["partSha256"].ToString();
        string relativePath = query["relativePath"].ToString();
        int maximumPart = 2 * 1024 * 1024;
        if (count < 0 || maximumPart < count || offset < 0 || length < offset || length - offset < count)
        {
            throw new ArgumentException("Invalid upload part range.", nameof(context));
        }
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, count));
        try
        {
            await context.Request.Body.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (await context.Request.Body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0
                || !string.Equals(Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))), partHash, StringComparison.Ordinal))
            {
                throw new IOException("The upload part could not be verified.");
            }
            using FileTransferClient transfer = await remote.OpenFileTransferAsync(target, cancellationToken).ConfigureAwait(false);
            for (int position = 0; position < count || position == 0; position += transfer.ChunkBytes)
            {
                ReadOnlyMemory<byte> chunk = buffer.AsMemory(position, Math.Min(transfer.ChunkBytes, count - position));
                FileChunkWriteRequest write = new(stage, relativePath, offset + position, chunk, Convert.ToHexString(SHA256.HashData(chunk.Span)), length, hash);
                await transfer.WriteAsync(write, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
            }
            return Results.Json(true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            slots.Release();
        }
    }
}
