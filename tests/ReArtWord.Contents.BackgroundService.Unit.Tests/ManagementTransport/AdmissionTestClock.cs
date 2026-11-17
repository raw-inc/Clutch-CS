namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>受付の試験で、PCの日時と実経過を独立して動かします。</summary>
internal sealed class AdmissionTestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long elapsed;

    /// <summary>要求を作成するPCの日時です。</summary>
    /// <returns>試験用のUTC日時です。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>日時補正の影響を受けない経過時間です。</summary>
    /// <returns>開始からのtick数です。</returns>
    public override long GetTimestamp() => elapsed;

    /// <summary>TimeSpanと同じ単位で進めます。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>PCの時計差または日時の補正を再現します。</summary>
    internal void Adjust(TimeSpan offset) => now += offset;

    /// <summary>通信・処理の待ち時間を進めます。</summary>
    internal void Advance(TimeSpan duration)
    {
        now += duration;
        elapsed += duration.Ticks;
    }
}
