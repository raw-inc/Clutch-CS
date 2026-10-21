using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>要求自体の永続受付を、さらに別の操作履歴へ複製せず比較更新します。</summary>
internal interface IRequestReceiptStore
{
    /// <summary>同一取引で版を照合し、受付または完了の現在値を耐久保存します。</summary>
    /// <returns>確定した項目、または競合・予約中ならnullです。保存結果不明は例外です。</returns>
    public ValueTask<DurableEntry?> CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken = default);
}
