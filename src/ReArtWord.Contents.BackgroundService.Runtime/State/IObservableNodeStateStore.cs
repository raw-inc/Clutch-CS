using ReArtWord.Contents.BackgroundService.Contracts.State;

namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>永続状態の変更を定期読取りせずに待ち受けます。</summary>
public interface IObservableNodeStateStore
{
    /// <summary>初期値を一度返し、その後は値の変更と削除を返します。</summary>
    /// <returns>購読の取消・終了で資源を解放する有限キューの変更列です。</returns>
    public IAsyncEnumerable<StateChange> WatchAsync(string scope, string key, CancellationToken cancellationToken = default);
}
