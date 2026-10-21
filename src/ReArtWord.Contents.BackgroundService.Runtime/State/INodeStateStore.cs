using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;

namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>明示した保存範囲の状態を比較更新する境界です。</summary>
public interface INodeStateStore
{
    /// <summary>状態とその版を読み取ります。</summary>
    /// <returns>状態がなければnullです。</returns>
    public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>期待した版と一致する場合だけ更新または削除します。</summary>
    /// <returns>比較と保存の実行結果です。</returns>
    public ValueTask<StateWriteResult> CompareExchangeAsync(
        string scope,
        string key,
        string? expectedVersion,
        NodeValue? value,
        CancellationToken cancellationToken = default);
}
