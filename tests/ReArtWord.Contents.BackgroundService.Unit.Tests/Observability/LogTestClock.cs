namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>保持境界だけを進める試験用時刻です。</summary>
internal sealed class LogTestClock : TimeProvider
{
    private readonly Lock gate = new();
    private long elapsed;
    private DateTimeOffset now = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    /// <summary>現在の試験時刻を返します。</summary>
    /// <returns>固定したUTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow()
    {
        using (gate.EnterScope())
        {
            return now;
        }
    }

    /// <summary>保持期限に使う経過時間を、日時と同じ速度で進めます。</summary>
    /// <returns>試験開始からのtick数です。</returns>
    public override long GetTimestamp()
    {
        using (gate.EnterScope())
        {
            return elapsed;
        }
    }

    /// <summary>TimeSpanと同じ単位で計測します。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>次に開始する保持処理の時刻を進めます。</summary>
    internal void Advance(TimeSpan duration)
    {
        using (gate.EnterScope())
        {
            now += duration;
            elapsed += duration.Ticks;
        }
    }
}
