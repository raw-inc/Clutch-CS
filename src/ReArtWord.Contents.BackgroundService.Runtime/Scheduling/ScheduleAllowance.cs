namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>通信中の時間幅を含め、予定回の許容期限がこのCoreで最も早く・遅く満了する位置を表します。</summary>
public readonly record struct ScheduleAllowance
{
    private readonly TimeProvider? clock;
    private readonly long sampled;
    private readonly TimeSpan earliest;
    private readonly TimeSpan latest;

    /// <summary>期限の範囲を一度だけ経過時計に結び付けます。既定値は元の期限が確認できない状態です。</summary>
    public ScheduleAllowance(TimeProvider clock, TimeSpan earliest, TimeSpan latest)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(latest, earliest);
        this.clock = clock;
        sampled = clock.GetTimestamp();
        this.earliest = earliest;
        this.latest = latest;
    }

    /// <summary>元の期限まで確実に残る時間です。</summary>
    public TimeSpan? Earliest => clock is null ? null
                                               : earliest - clock.GetElapsedTime(sampled);

    /// <summary>元の期限まで残り得る最長時間です。</summary>
    public TimeSpan? Latest => clock is null ? null
                                             : latest - clock.GetElapsedTime(sampled);
}
