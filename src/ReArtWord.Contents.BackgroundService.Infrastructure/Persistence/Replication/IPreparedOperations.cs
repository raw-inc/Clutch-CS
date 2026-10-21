namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>現在状態や過去本文を走査せず、未確定の共有操作だけを有限件数で確認します。</summary>
internal interface IPreparedOperations
{
    /// <summary>前回位置より後の準備を返し、空集合なら呼出元が先頭へ戻します。</summary>
    /// <returns>元操作のIDです。準備本文や業務値は返しません。</returns>
    public ValueTask<IReadOnlyList<Guid>> ReadPreparedIdsAsync(Guid after, int maximum, CancellationToken cancellationToken = default);

    /// <summary>自分の準備が終わっていても、全参加者の確定確認が残る操作を巡回へ含めます。</summary>
    /// <returns>準備または新形式の確定判断を持つ元操作です。</returns>
    public ValueTask<IReadOnlyList<Guid>> ReadRecoveryIdsAsync(Guid after, int maximum, CancellationToken cancellationToken = default);
}
