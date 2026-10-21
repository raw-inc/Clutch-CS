using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>現在値と指紋は固定し、本文が必要な期間だけ読取り取引を開きます。</summary>
internal sealed class FrozenSnapshotExport(ISnapshotExportSource source, SnapshotExport description)
{
    /// <summary>本文を復元しなくても照合できる固定情報です。</summary>
    internal SnapshotExport Description => description;

    /// <summary>確認済みの元本文だけを参照し、成功・取消・失敗時とも読取り資源を解放します。</summary>
    /// <returns>固定版から選んだ本文の処理結果です。</returns>
    internal Task<T> ReadAsync<T>(Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken = default)
        => source.ReadExportAsync(current =>
        {
            Dictionary<Guid, SnapshotReceipt> available = current.Receipts.ToDictionary(item => item.OperationId);
            SnapshotReceipt[] references = description.Receipts.Select(fixedReceipt => new SnapshotReceipt(fixedReceipt.OperationId,
                () => ReadVerified(fixedReceipt, available, current.ReadHistoricalReceipt, cancellationToken), _ => fixedReceipt.Hash())).ToArray();
            return read(new(description.Snapshot, references));
        }, cancellationToken);

    /// <summary>本文を読む場合だけ、固定した元操作・内容指紋との一致を確認します。</summary>
    /// <returns>同じ版を保証できた本文です。整理や置換は不完全な書出しにしません。</returns>
    private static DurableReceipt ReadVerified(SnapshotReceipt expected, Dictionary<Guid, SnapshotReceipt> available,
        Func<Guid, DurableReceipt?>? readHistoricalReceipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DurableReceipt? value;
        string? actualHash;
        if (!available.TryGetValue(expected.OperationId, out SnapshotReceipt? actual))
        {
            // 固定後に圧縮へ移った本文も同じ読取り取引で照合し、削除・置換とは区別します。
            value = readHistoricalReceipt?.Invoke(expected.OperationId);
            actualHash = value is null ? null
                                       : ContractJson.Hash(value);
        }
        else
        {
            value = actual.Read();
            actualHash = actual.Hash(value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null || value.OperationId != expected.OperationId || !string.Equals(expected.Hash(), actualHash, StringComparison.Ordinal))
        {
            throw new SnapshotChangedException();
        }
        return value;
    }
}
