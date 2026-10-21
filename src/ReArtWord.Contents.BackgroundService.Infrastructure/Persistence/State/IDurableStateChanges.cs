namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>同じ保存先で確定した状態の変更を、定期読取りなしで購読します。</summary>
public interface IDurableStateChanges
{
    /// <summary>初期読取りより先に購読を登録します。</summary>
    /// <returns>呼出し元が解放する有限の変更キューです。</returns>
    public DurableStateSubscription SubscribeState(string scope, string key);
}
