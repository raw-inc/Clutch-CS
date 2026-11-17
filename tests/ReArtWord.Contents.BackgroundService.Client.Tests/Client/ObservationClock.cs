namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示用日時の補正と、通信・観測の経過時間を別々に進めます。</summary>
internal sealed class ObservationClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long ticks;

    /// <summary>現在の表示用日時です。</summary>
    /// <returns>試験用UTC日時です。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>補正の影響を受けない経過時間です。</summary>
    /// <returns>開始からのtick数です。</returns>
    public override long GetTimestamp() => ticks;

    /// <summary>TimeSpanと同じ単位を使います。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>OS時刻の補正だけを再現します。</summary>
    internal void Adjust(TimeSpan difference) => now += difference;

    /// <summary>実際の時間経過を再現します。</summary>
    internal void Advance(TimeSpan elapsed)
    {
        now += elapsed;
        ticks += elapsed.Ticks;
    }
}
