namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>複製開始と確定判断を、それぞれ元の局所変更と同じ耐久取引へまとめます。</summary>
internal interface IReplicationTransactionStore
{
    /// <summary>原子的に保存できる場合だけ書き込み、未対応なら一切変更せずnullを返します。</summary>
    /// <returns>耐久確認後の結果です。書込み後の不明結果は例外とし、別経路へ再送しません。</returns>
    public ValueTask<ReplicationStartResult?> TryStartAsync(ReplicationStart start, CancellationToken cancellationToken = default);

    /// <summary>局所コミットとその送信判断を一括保存し、未対応の場合は変更せずnullを返します。</summary>
    /// <returns>元の局所結果、または従来経路を使うnullです。</returns>
    public ValueTask<Receipts.DurableReceipt?> TryCommitAsync(ReplicationCommit commit, CancellationToken cancellationToken = default);
}
