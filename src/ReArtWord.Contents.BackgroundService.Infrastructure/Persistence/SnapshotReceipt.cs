using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>履歴の内容識別と本文取得を分け、同じ本文を照合だけのために復元しません。</summary>
internal sealed record SnapshotReceipt(Guid OperationId, Func<DurableReceipt> Read, Func<DurableReceipt?, string> CalculateHash)
{
    /// <summary>本文を既に読んでいる書出しでは、その同じ値で指紋も計算します。</summary>
    /// <returns>本文のcanonical指紋です。</returns>
    internal string Hash(DurableReceipt? value = null) => CalculateHash(value);

    /// <summary>既に所有している固定値を、ストリーム対応の参照と同じ形で扱います。</summary>
    /// <returns>固定値の参照です。</returns>
    internal static SnapshotReceipt FromValue(DurableReceipt value) => new(value.OperationId, () => value, _ => ContractJson.Hash(value));
}
