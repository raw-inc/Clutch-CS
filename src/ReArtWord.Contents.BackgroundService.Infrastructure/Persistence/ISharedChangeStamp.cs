namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>短い再試行待ちを早期解除するための、本文を復元しない変更通知位置です。</summary>
internal interface ISharedChangeStamp
{
    /// <summary>比較保存・本文検査の代わりには使わず、通常確認の再開判断だけへ返します。</summary>
    /// <returns>不透明な世代です。安価な確認に未対応の媒体はnullです。</returns>
    public ValueTask<string?> ReadSharedChangeStampAsync(CancellationToken cancellationToken = default);
}
