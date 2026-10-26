using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>ログ出力の受付・状態・取消・保存を既存のHTTP待受内で提供します。</summary>
internal static class LogExportRoutes
{
    /// <summary>本文をブラウザーのヒープへ展開せず、標準ダウンロードへ接続します。</summary>
    internal static void Map(WebApplication app)
    {
        RouteGroupBuilder routes = app.MapGroup("/client/log-exports");
        routes.AddEndpointFilter(async (context, next) =>
        {
            string crossSite = "cross-site", siteHeader = "Sec-Fetch-Site";
            if (string.Equals(context.HttpContext.Request.Headers[siteHeader], crossSite, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return await next(context).ConfigureAwait(false);
        });
        routes.MapPost("/", (LogExportRequest request, LogExportJobs jobs) => jobs.Create(request));
        routes.MapGet("/{id:guid}", (Guid id, LogExportJobs jobs) =>
        {
            if (jobs.Read(id) is { } status)
            {
                return Results.Ok(status);
            }
            return Results.NotFound();
        });
        routes.MapPost("/{id:guid}/cancel", async (Guid id, LogExportJobs jobs, CancellationToken token) =>
        {
            await jobs.CancelAsync(id, token).ConfigureAwait(false);
            return Results.NoContent();
        });
        routes.MapGet("/{id:guid}/download", (Guid id, LogExportJobs jobs, HttpContext context) =>
        {
            string queued = "queued";
            if (jobs.Read(id) is { } status && string.Equals(status.State, queued, StringComparison.Ordinal))
            {
                context.Response.Headers["X-Accel-Buffering"] = "no";
                string contentType = status.Format switch { ELogExportFormat.Log => "text/plain; charset=utf-8", _ => "application/zip" };
                return Results.Stream(output => SendAsync(jobs, id, output, context, context.RequestAborted), contentType, fileDownloadName: status.FileName);
            }
            return Results.NotFound();
        });
    }

    /// <summary>送信中の失敗を正常終了として渡さず、切断時は取得処理も終了させます。</summary>
    /// <returns>送信完了または接続の中断です。</returns>
    // 抑制理由: HTTP応答が始まった後はエラー本文へ置換できないため、要求接続を中断します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "出力単位の失敗は状態に記録済みで、HTTP接続を明示的に中断します。")]
    private static async Task SendAsync(LogExportJobs jobs, Guid id, Stream output, HttpContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await jobs.WriteAsync(id, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            context.Abort();
        }
    }
}
