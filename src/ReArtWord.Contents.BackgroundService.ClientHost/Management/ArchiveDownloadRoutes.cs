using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>完成したアーカイブをブラウザーの標準ダウンロードへ逐次転送します。</summary>
internal static class ArchiveDownloadRoutes
{
    private static readonly Action<ILogger, Guid, Exception?> reportFailure = LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(402, "ArchiveDownloadFailed"),
        "Archive {OperationId} download failed.");

    /// <summary>パスを受け取らず、選択済みのクラスター・ピア・実体だけを照会します。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/client/archives/{id:guid}/download", async (Guid id, string cluster, string peer, Guid instance, ObserverConnection connection, HttpContext context) =>
        {
            string crossSite = "cross-site", siteHeader = "Sec-Fetch-Site";
            if (string.Equals(context.Request.Headers[siteHeader], crossSite, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            UdpObserver observer = connection.FindObserver(cluster, peer) ?? throw new InvalidOperationException("The selected archive peer is unavailable.");
            async Task<JsonElement> ReadAsync(string operation, object body, CancellationToken token)
            {
                ManagementReply reply = await observer.InspectWithRetryAsync(new(Guid.NewGuid(), cluster, peer, instance, DateTimeOffset.UtcNow.AddMinutes(1),
                    operation, NodeValue.FromObject(body)), token).ConfigureAwait(false);
                if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
                {
                    throw new IOException("The archive read was not confirmed.");
                }
                return reply.Value.Json;
            }
            JsonElement response = await ReadAsync("archive.operation", id, context.RequestAborted).ConfigureAwait(false);
            ArchiveOperationResult? result = response.Deserialize<ArchiveOperationResult>(ContractJson.Options);
            if (result?.OperationId != id || !result.Completed || result.Outcome != EOutcome.Succeeded || result.File is null)
            {
                return Results.NotFound();
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = "\"" + result.File.Sha256 + "\"";
            return Results.Stream(output => SendAsync(output, result.File, ReadAsync, context, app.Logger, context.RequestAborted),
                "application/zip", fileDownloadName: "Clutch-CS-archive-" + id.ToString("N") + ".zip");
        });
    }

    /// <summary>欠落や検査値不一致があれば、正常なダウンロードとして終了させません。</summary>
    /// <returns>全体検証を伴う転送、またはHTTP接続の中断です。</returns>
    // 抑制理由: HTTP本文の開始後はエラー本文へ変更できないため、診断を残して接続を中断します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "出力の障害を診断し、HTTP応答を明示的に中断します。")]
    private static async Task SendAsync(Stream output, ArchiveTransfer file, Func<string, object, CancellationToken, Task<JsonElement>> read,
        HttpContext context, ILogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            await ArchiveFileTransfer.DownloadAsync(output, file, read, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            reportFailure(logger, file.OperationId, exception);
            context.Abort();
        }
    }
}
