namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>固定した読取り取引の寿命内で、一件ずつ履歴本文を参照します。</summary>
internal interface ISnapshotExportSource
{
    /// <summary>現在値を固定した後、同じ操作の不変な履歴本文だけを再取得できる媒体かです。</summary>
    public bool SupportsDeferredReceiptReads => false;

    /// <summary>呼出しが終わるまで同じ版を維持し、履歴本文を集合として保持しません。</summary>
    /// <returns>固定した版を使用した処理の結果です。</returns>
    public Task<T> ReadExportAsync<T>(Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken = default);
}
