namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>参加者と受付期限の検証では時刻を固定し、復旧の確認では進めます。</summary>
internal sealed class RequestTestClock : TimeProvider
{
    private readonly Lock gate = new();
    private DateTimeOffset? frozen;
    private TimeSpan offset;

    /// <summary>受付期限とPeerの観測に同じ時刻を返します。</summary>
    /// <returns>固定中の時刻、または経過時間を反映したUTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow()
    {
        using (gate.EnterScope())
        {
            return frozen ?? TimeProvider.System.GetUtcNow() + offset;
        }
    }

    /// <summary>開始前に参加者や受付期限が変わらないよう、試験時刻を固定します。</summary>
    internal void Freeze()
    {
        using (gate.EnterScope())
        {
            frozen ??= TimeProvider.System.GetUtcNow() + offset;
        }
    }

    /// <summary>指定時間だけ進め、結果照会やPeer復旧に必要な時計を再開します。</summary>
    internal void AdvanceAndResume(TimeSpan amount)
    {
        using (gate.EnterScope())
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            offset = (frozen ?? now + offset) + amount - now;
            frozen = null;
        }
    }
}
