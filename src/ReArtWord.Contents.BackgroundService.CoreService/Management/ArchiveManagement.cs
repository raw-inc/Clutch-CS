using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>アーカイブの転送・確認・開始・元結果照会を既存管理経路へ公開します。</summary>
internal sealed class ArchiveManagement(ClusterArchiveService archives, ILogger<ArchiveManagement> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly Action<ILogger, Exception?> reportFailure = LoggerMessage.Define(LogLevel.Error, new EventId(401, "ArchiveOperationFailed"),
        "Archive operation failed; inspect the original operation result.");

    /// <summary>読み取りと、元IDを維持する明示操作を登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Write("archive.export", async (body, token) => await archives.ExportAsync(ManagementRegistry.Body<ArchiveExportRequest>(body), token).ConfigureAwait(false));
        registry.Write("archive.upload.begin", async (body, token) => await archives.BeginUploadAsync(ManagementRegistry.Body<ArchiveTransfer>(body), token).ConfigureAwait(false));
        registry.Write("archive.upload.chunk", async (body, token) => await archives.UploadAsync(ManagementRegistry.Body<ArchiveChunk>(body), token).ConfigureAwait(false));
        registry.Write("archive.preview", async (body, token) => await archives.PreviewAsync(ManagementRegistry.Body<ArchivePreviewRequest>(body), token).ConfigureAwait(false));
        registry.Write("archive.import", async (body, token) => await archives.ImportAsync(ManagementRegistry.Body<ArchiveImportRequest>(body), token).ConfigureAwait(false));
        registry.Read("archive.operation", async (body, token) => await archives.ReadAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Read("archive.read", async (body, token) => await archives.ReadChunkAsync(ManagementRegistry.Body<ArchiveReadRequest>(body), token).ConfigureAwait(false));
        registry.Write("archive.reconcile", async (body, token) => await archives.ReconcileAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
    }

    /// <summary>画面の切断後も処理を所有し、停止時は元結果を保存します。</summary>
    /// <returns>このHostが受け付けた処理の寿命です。</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => archives.RunAsync(exception => reportFailure(logger, exception), stoppingToken);
}
