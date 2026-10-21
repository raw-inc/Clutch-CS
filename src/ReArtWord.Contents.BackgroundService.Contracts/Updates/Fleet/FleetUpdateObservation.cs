namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>更新所有者の実体と単調な通知番号で、遅延した進捗の巻き戻りを防ぎます。</summary>
public sealed record FleetUpdateObservation(Guid PublisherId, long Sequence, FleetUpdateActivity Activity, bool Stale = false)
{
    /// <summary>同じ所有者では通知順、再起動後の所有者では保存時刻を比較します。</summary>
    /// <returns>既知の進捗より新しい場合だけtrueです。</returns>
    public bool IsNewerThan(FleetUpdateObservation previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (PublisherId == previous.PublisherId)
        {
            return previous.Sequence < Sequence;
        }
        return previous.Activity.UpdatedAt < Activity.UpdatedAt;
    }
}
