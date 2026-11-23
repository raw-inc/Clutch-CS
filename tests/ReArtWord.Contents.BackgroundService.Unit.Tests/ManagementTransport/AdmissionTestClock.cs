namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>UTC日時と単調時計を別々に変更し、PCの時計差と時間の経過を区別して再現します。</summary>
internal sealed class AdmissionTestClock : TimeProvider
{
    private long utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private long elapsed;

    /// <summary>試験対象のPCが持つUTC日時です。</summary>
    /// <returns>試験用のUTC日時です。</returns>
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    /// <summary>日時補正の影響を受けない経過時間です。</summary>
    /// <returns>開始からのtick数です。</returns>
    public override long GetTimestamp() => Interlocked.Read(ref elapsed);

    /// <summary>TimeSpanと同じ単位で進めます。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>PCの時計差または日時の補正を再現します。</summary>
    internal void Adjust(TimeSpan offset) => Interlocked.Add(ref utcTicks, offset.Ticks);

    /// <summary>UTC日時と単調時計を同じ時間だけ進め、通信や処理の待ち時間を再現します。</summary>
    internal void Advance(TimeSpan duration)
    {
        Adjust(duration);
        Interlocked.Add(ref elapsed, duration.Ticks);
    }
}
