using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>画面境界の入力・通信障害へ秘密を含む例外本文を出しません。</summary>
internal static class ClientErrors
{
    /// <summary>送信失敗を元操作の未実行・成功として捏造せず応答します。</summary>
    /// <returns>既知の応答または一般化した障害です。</returns>
    // 抑制理由: 画面への最終境界で例外本文を秘匿し、元要求の照会情報だけを維持します。
    [SuppressMessage("Design", "CA1031", Justification = "最終HTTP境界で任意障害を一般化した応答へ変換します。")]
    public static async Task HandleAsync(HttpContext context, RequestDelegate next, CancellationToken cancellationToken = default)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 画面切断後もCoreが受理した操作は継続し得るため再送しません。
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            int status = StatusCodes.Status503ServiceUnavailable;
            if (exception is ArgumentException or FormatException or JsonException or BadHttpRequestException)
            {
                status = StatusCodes.Status400BadRequest;
            }
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new
            {
                reason = "Client.RequestUnconfirmed", commandId = context.Response.Headers["X-Contents-Command"].ToString(),
                message = "選択したNIC・Cluster・Peerと入力を確認してください。送信済み操作は元の操作IDで結果を照会してください。",
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
