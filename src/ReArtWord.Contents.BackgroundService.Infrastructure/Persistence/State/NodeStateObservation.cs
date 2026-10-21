using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>保存先の変更から、版だけの更新を除いた値の変更を取り出します。</summary>
internal static class NodeStateObservation
{
    /// <summary>購読を初期読取りより先に登録し、更新との隙間を作りません。</summary>
    /// <returns>初期値と確定変更の列です。</returns>
    internal static async IAsyncEnumerable<StateChange> WatchAsync(IDurableRepository repository, string scope, string key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (repository is not IDurableStateChanges changes)
        {
            throw new NotSupportedException("この保存先は状態の変更通知を提供していません。");
        }
        using DurableStateSubscription subscription = changes.SubscribeState(scope, key);
        DurableEntry? entry = subscription.Initialize(await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false));
        NodeValue? previous = entry?.Value;
        yield return new(key, entry?.Version, entry?.Value?.Copy(), DateTimeOffset.UtcNow, Initial: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            entry = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Same(previous, entry.Value))
            {
                continue;
            }
            previous = entry.Value;
            yield return new(key, entry.Version, entry.Value?.Copy(), DateTimeOffset.UtcNow);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>BinaryのBase64化やJSONの全再シリアル化をせず、値の変化を判定します。</summary>
    /// <returns>削除と保存済みnullも区別した値の一致です。</returns>
    private static bool Same(NodeValue? first, NodeValue? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        if (first.Kind != second.Kind || (first.DataType is not null || second.DataType is not null) && !ValueTypeInference.Same(first.DataType, second.DataType))
        {
            return false;
        }
        if (first.Kind == EValueKind.Binary)
        {
            return first.Binary.Span.SequenceEqual(second.Binary.Span);
        }
        return JsonElement.DeepEquals(first.Json, second.Json);
    }
}
