using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;

/// <summary>全対象の退避確認後に元の実行IDでファイル操作を開始します。</summary>
public interface IFileTransactionDispatcher
{
    /// <summary>固定全対象を実行し、非成功時に確認できる範囲を補償します。</summary>
    /// <returns>実行と補償を分けた操作結果です。</returns>
    public Task<FileTransactionResult> ExecuteAsync(FileTransactionRequest request, CancellationToken cancellationToken = default);
}
