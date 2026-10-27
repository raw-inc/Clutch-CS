namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>要求の受付待ちだけ時刻を固定し、参加や復旧の待ち時間は進めます。</summary>
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

    /// <summary>参加完了後、送信から実処理の開始まで受付期限が切れないようにします。</summary>
    internal void Freeze()
    {
        using (gate.EnterScope())
        {
            frozen ??= TimeProvider.System.GetUtcNow() + offset;
        }
    }

    /// <summary>受付確認後に指定時間だけ進め、結果照会やPeer復旧に必要な時計を再開します。</summary>
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
