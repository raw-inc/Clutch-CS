using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Journal;

/// <summary>操作ごとの保存をブラウザーのIndexedDBトランザクションへ委譲します。</summary>
internal sealed class OperationJournalStorage(IJSRuntime script)
{
    /// <summary>旧履歴を保存完了後に移行し、再照会に必要な全記録を読みます。</summary>
    /// <returns>端末に保存した操作記録です。</returns>
    internal async Task<IReadOnlyList<OperationRecord>> ReadAsync(CancellationToken cancellationToken = default)
    {
        string text = await script.InvokeAsync<string>("contentsOperationStore.read", cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<OperationRecord>>(text, ContractJson.Options) ?? [];
    }

    /// <summary>新しい要求を送信する前に保存し、既存IDの再送を拒否します。</summary>
    /// <returns>保存完了した要求です。</returns>
    internal Task<OperationRecord> CreateAsync(OperationRecord item, CancellationToken cancellationToken = default) => WriteAsync(item, "create", cancellationToken);

    /// <summary>既知IDの照会を記録します。別画面の元要求・接続先は上書きしません。</summary>
    /// <returns>既存記録、または新しく記録した照会です。</returns>
    internal Task<OperationRecord> TrackAsync(OperationRecord item, CancellationToken cancellationToken = default) => WriteAsync(item, "track", cancellationToken);

    /// <summary>元要求を保持し、その操作の確認結果だけを更新します。</summary>
    /// <returns>保存完了した操作記録です。</returns>
    internal Task<OperationRecord> UpdateAsync(OperationRecord item, CancellationToken cancellationToken = default) => WriteAsync(item, "update", cancellationToken);

    /// <summary>一件の変更だけを送り、ブラウザーの保存確定を待ちます。</summary>
    /// <returns>トランザクションで確定した記録です。</returns>
    private async Task<OperationRecord> WriteAsync(OperationRecord item, string mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        string text = await script.InvokeAsync<string>("contentsOperationStore.write", cancellationToken, JsonSerializer.Serialize(item, ContractJson.Options), mode).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OperationRecord>(text, ContractJson.Options)!;
    }
}
