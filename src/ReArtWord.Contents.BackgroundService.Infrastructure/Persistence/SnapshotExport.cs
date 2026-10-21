using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>現在値と小さい履歴索引だけを保持し、本文は読取り取引から必要なときに取得します。</summary>
internal sealed record SnapshotExport(RepositorySnapshot Snapshot, IReadOnlyList<SnapshotReceipt> Receipts, Func<Guid, Receipts.DurableReceipt?>? ReadHistoricalReceipt = null)
{
    /// <summary>対応媒体では現在値と履歴の指紋だけを固定し、ネットワーク待ちへ読取り取引を持ち越しません。</summary>
    /// <returns>必要本文を後から再検証できる固定参照、または従来経路を使うnullです。</returns>
    internal static Task<FrozenSnapshotExport?> FreezeAsync(IDurableRepository repository, CancellationToken cancellationToken = default)
    {
        if (repository is not ISnapshotExportSource { SupportsDeferredReceiptReads: true } source)
        {
            return Task.FromResult<FrozenSnapshotExport?>(null);
        }
        return source.ReadExportAsync<FrozenSnapshotExport?>(current =>
        {
            List<SnapshotReceipt> receipts = [];
            foreach (SnapshotReceipt receipt in current.Receipts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string hash = receipt.Hash();
                receipts.Add(new(receipt.OperationId, MissingBody, _ => hash));
            }
            return Task.FromResult<FrozenSnapshotExport?>(new(source, new(current.Snapshot, receipts)));
        }, cancellationToken);
    }

    /// <summary>照合情報を本文の所有値として誤使用しないための境界です。</summary>
    /// <returns>書出し用の読取り取引が必要であることを示す例外です。</returns>
    private static Receipts.DurableReceipt MissingBody() => throw new InvalidOperationException("Snapshot descriptions do not contain receipt bodies.");

    /// <summary>対応媒体では取引内で読み、既存媒体では従来の固定スナップショットを使用します。</summary>
    /// <returns>同じ固定版を使用した処理結果です。</returns>
    internal static async Task<T> ReadAsync<T>(IDurableRepository repository, Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken = default)
    {
        if (repository is ISnapshotExportSource source)
        {
            return await source.ReadExportAsync(read, cancellationToken).ConfigureAwait(false);
        }
        RepositorySnapshot snapshot = await repository.ExportAsync(cancellationToken).ConfigureAwait(false);
        return await read(new(snapshot with { Receipts = [] }, snapshot.Receipts.Select(SnapshotReceipt.FromValue).ToArray())).ConfigureAwait(false);
    }
}

