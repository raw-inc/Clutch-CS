namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>一回の反復内で、最初の開始を基準に一枠一要素の開始時刻を計算します。</summary>
internal sealed class IterationStartSlots(TimeProvider clock, TimeSpan interval)
{
    private long? first;
    private TimeSpan next;

    /// <summary>空きができた時点の次の刻みを固定し、タイマーの遅着で待機枠をずらし続けません。</summary>
    /// <returns>今回の枠までの残り時間です。</returns>
    internal TimeSpan Remaining(ref TimeSpan? scheduled)
    {
        if (first is not long origin)
        {
            return TimeSpan.Zero;
        }
        TimeSpan elapsed = clock.GetElapsedTime(origin);
        long slot = elapsed.Ticks / interval.Ticks;
        if (elapsed.Ticks % interval.Ticks != 0)
        {
            slot++;
        }
        scheduled ??= TimeSpan.FromTicks(Math.Max(next.Ticks, slot * interval.Ticks));
        return scheduled.Value - elapsed;
    }

    /// <summary>開始係の排他内で実際に使った枠を記録し、過去の未使用枠は再利用しません。</summary>
    internal void Started()
    {
        first ??= clock.GetTimestamp();
        long elapsed = clock.GetElapsedTime(first.Value).Ticks;
        next = TimeSpan.FromTicks((elapsed / interval.Ticks + 1) * interval.Ticks);
    }
}
