namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>更新所有者の実体と単調な通知番号で、遅延した進捗の巻き戻りを防ぎます。</summary>
public sealed record FleetUpdateObservation(Guid PublisherId, long Sequence, FleetUpdateActivity Activity, bool Stale = false)
{
    /// <summary>同じ発行元では保存済みの通知順を使います。発行元が異なる場合は、同じ操作の終了を確認できたときだけ進めます。</summary>
    /// <returns>既知の進捗より新しい場合だけtrueです。</returns>
    public bool IsNewerThan(FleetUpdateObservation previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (PublisherId == previous.PublisherId)
        {
            return previous.Sequence < Sequence;
        }
        return Activity.OperationId == previous.Activity.OperationId && !previous.Activity.Completed && Activity.Completed && !Stale;
    }
}
