using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>通常get/setだけに用途別の保存状態を追加し、強いCAS予約とは分けます。</summary>
public interface IAvailableNodeStateStore : INodeStateStore
{
    /// <summary>保存状態とオフライン方針を伴う通常値を取得します。</summary>
    /// <returns>値と保存・現在有効・復旧の版です。</returns>
    public ValueTask<AvailabilityValue> ReadAvailableAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>明示用途方針で通常値を変更します。</summary>
    /// <returns>永続保存とローカル採用を分離した結果です。</returns>
    public ValueTask<AvailabilityWriteResult> SetAvailableAsync(string scope, string key, NodeValue value, string? expectedRevision, Guid operationId, CancellationToken cancellationToken = default);
}
