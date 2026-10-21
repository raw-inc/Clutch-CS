using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Files;

/// <summary>ブラウザーの保存処理へ、対象Coreの検証済みファイルを有限バッファで中継します。</summary>
internal static class BrowserFileDownloads
{
    /// <summary>同一サイトのダウンロードリンクをブラウザー標準の保存へ接続します。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/client/browser-files/download", DownloadAsync);
    }

    /// <summary>開始時のPeer実体と内容版を固定してから応答を開始します。</summary>
    /// <returns>ブラウザーへ流すファイル応答です。</returns>
    private static async Task<IResult> DownloadAsync(HttpContext context, PeerManagementClient remote, CancellationToken cancellationToken = default)
    {
        string site = context.Request.Headers["Sec-Fetch-Site"].ToString(), crossSite = "cross-site";
        if (string.Equals(site, crossSite, StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        IQueryCollection query = context.Request.Query;
        FileTransferPeer target = new(query["clusterId"].ToString(), query["peerId"].ToString(), Guid.Parse(query["instanceId"].ToString()));
        string path = query["path"].ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileTransferClient transfer = await remote.OpenFileTransferAsync(target, cancellationToken).ConfigureAwait(false);
        FileEntryInfo file = await transfer.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (file.IsDirectory)
        {
            return Results.BadRequest(new { reason = "Select a file to download." });
        }
        context.Response.ContentLength = file.Length;
        return Results.Stream(output => CopyAsync(remote, target, file, output, cancellationToken), "application/octet-stream",
            fileDownloadName: path.Replace('\\', '/').Split('/')[^1]);
    }

    /// <summary>各転送片と全体のハッシュを確認し、不一致なら完了したダウンロードとして扱わせません。</summary>
    /// <returns>最後の検証済み転送片までの送信です。</returns>
    internal static async Task CopyAsync(PeerManagementClient remote, FileTransferPeer target, FileEntryInfo file, Stream output, CancellationToken cancellationToken = default)
    {
        using FileTransferClient transfer = await remote.OpenFileTransferAsync(target, cancellationToken).ConfigureAwait(false);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        do
        {
            FileChunk chunk = await transfer.ReadAsync(new FileChunkRequest(file.Path, offset, transfer.ChunkBytes, file.Length, file.Sha256), cancellationToken).ConfigureAwait(false);
            hash.AppendData(chunk.Data.Span);
            offset += chunk.Data.Length;
            if (offset == file.Length && !string.Equals(file.Sha256, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.Ordinal))
            {
                throw new IOException("The source content changed during download.");
            }
            await output.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        while (offset < file.Length);
    }
}
