using ReArtWord.Contents.BackgroundService.Contracts.State;

namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>共有キーのTTL判定と比較保存を、同じ状態所有者に依頼します。</summary>
public interface ISharedDedupeStore
{
    /// <summary>元のTTLの失効を確認できた場合だけ次の入力を確保します。</summary>
    /// <returns>確保、既存キー、または確認不能です。</returns>
    public ValueTask<StateWriteResult> ReserveAsync(string scope, string key, TimeSpan? lifetime, CancellationToken cancellationToken = default);
}
